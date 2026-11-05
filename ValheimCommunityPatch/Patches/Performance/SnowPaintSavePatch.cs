using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Snow Paint Saves: Deep North snow paint saves a zone's terrain at most once every few
    // seconds, and only refreshes the ground and grass when the change could be seen.
    //
    // Every burning fire_pit, hearth and bonfire melts the snow around it five times a second
    // (Fireplace.UpdateSnowMelt, fire_pit_iron every 2.5 s), and footsteps and shovels paint snow
    // the same way. Each operation ends in TerrainComp.DoOperation, which saves the zone's whole
    // terrain, queues a rebuild of its paint and resets the grass. Save has a guard for paint that
    // did not change, ComputePaintMaskHash, but that hash is an exact sum of floats and snow paint
    // never holds still: these operations set m_pointMultiplicationFactor, so the depth fades by a
    // fraction of itself each time and does not reach zero. Paint read back from an 8-bit texture
    // would stop it, but getMask only reads the texture when no rebuild is pending, so as soon as
    // two operations land on a zone in one frame the second reads the raw float and the value moves
    // again. A base with 14 hearths on a player's world measured about 78 full saves a second, each
    // writing and gzipping a whole zone, for over 40% of the main thread. Refilling the snow, as
    // Deep North Snowfall does on waking, starts it all again. Other peers in range
    // reload the zone and rebuild it in full, collision included, as those saves reach them.
    //
    // A transpiler on DoOperation routes its three calls here, for snow paint only:
    //  - The paint is compared at 8-bit precision, the precision the paint texture and everything
    //    that reads it has. When no step changed anywhere in the zone, nothing can look different,
    //    so the save, the rebuild and the grass reset are all skipped. Skipping the rebuild also
    //    leaves getMask reading the texture, so the fade settles instead of creeping on for hours.
    //    On that base, snow around the fires ended within a few steps of vanilla's.
    //  - A change that can be seen refreshes the ground at once, so the player sees no delay. The
    //    first save in a quiet zone runs immediately and later ones wait for 'Snow Paint Save
    //    Interval'. The paint waits in memory, which is what the zone draws from anyway.
    //
    // Nothing is left unsaved: a waiting zone saves when its interval is up, at most one a frame so
    // many zones coming due cannot stack into one spike, and all of them save before a world save.
    // A compiler being unloaded saves on the way out. By then ZNetScene has already reset its view,
    // so the ZDO recorded when the save was put off is lent back for that one call, after checking
    // it is still this zone's and still owned here. What can be lost is at most one interval of
    // paint on a peer that disconnects or gives up the zone first, which a fire simply melts again.
    // A dedicated server's world save only holds what clients have sent it, so it can be up to one
    // interval behind them: the hook runs on the saving process, and clients are not told to flush.
    //
    // Only paint-only operations with m_pointMultiplicationFactor above zero take this path: in the
    // game that is exactly the snow_fire, snow_shovel, snow_tree and snow_walk prefabs. The hoe,
    // cultivator, roads, level and raise operations, and any other mod's, keep vanilla's behavior.
    //
    // Both: whichever peer owns a zone's terrain compiler runs DoOperation, a host, a dedicated
    // server or a client, and it changes nothing peers need to agree on. Other peers see melting a
    // few seconds later than they would have.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(TerrainComp))]
    internal static class SnowPaintSavePatch {
        private const string FixName = "Fix Snow Paint Saves";
        private const string Site = "TerrainComp.DoOperation";

        internal static FixToggle Enabled;
        internal static ConfigEntry<float> SaveInterval;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SnowPaintSavePatch),
                ValConfig.SectionPerformance,
                FixName,
                true,
                "Limits how often Deep North snow saves the ground. Every burning fire pit, hearth and " +
                "bonfire melts the snow around it five times a second, and in vanilla each of those can " +
                "save the whole zone, which with a dozen fires in a base can take the game down to a few " +
                "frames a second. With this on, snow only saves and refreshes the ground when the change " +
                "could be seen, and saves a zone at most once per 'Snow Paint Save Interval'.");

            SaveInterval = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Snow Paint Save Interval",
                5f,
                "With Fix Snow Paint Saves on, the most often snow saves a zone's terrain, in seconds. " +
                "Other players see melting and footprints up to this much later. 0 saves every visible change.",
                advanced: true, valMin: 0f, valMax: 60f);
        }

        private sealed class Tile {
            public TerrainComp Comp;
            public bool HasSeen;
            public int Seen;
            public float NextSave;
            public bool Waiting;
            public ZDO Zdo;
            public ZDOID ZdoId;
        }

        private static readonly MethodInfo SaveMethod = AccessTools.DeclaredMethod(typeof(TerrainComp), "Save");
        private static readonly MethodInfo PokeMethod = AccessTools.Method(typeof(Heightmap), nameof(Heightmap.Poke), new[] { typeof(int), typeof(bool) });
        private static readonly MethodInfo ResetGrassMethod = AccessTools.Method(typeof(ClutterSystem), nameof(ClutterSystem.ResetGrass));
        private static readonly MethodInfo LimitedSaveMethod = AccessTools.DeclaredMethod(typeof(SnowPaintSavePatch), nameof(LimitedSave));
        private static readonly MethodInfo LimitedPokeMethod = AccessTools.DeclaredMethod(typeof(SnowPaintSavePatch), nameof(LimitedPoke));
        private static readonly MethodInfo LimitedResetGrassMethod = AccessTools.DeclaredMethod(typeof(SnowPaintSavePatch), nameof(LimitedResetGrass));

        // Keyed on GetInstanceID(); see TeardownHooks for the rationale and invariant.
        private static readonly Dictionary<int, Tile> Tiles = new Dictionary<int, Tile>();
        private static readonly List<Tile> WaitingTiles = new List<Tile>();

        // Set by LimitedSave and read by the two calls right after it in the same DoOperation.
        private static bool _nothingVisible;

        // A put-off save needs something to run it, so without these hooks every save stays vanilla's.
        private static readonly HookHealth Hooks = new HookHealth(
            typeof(SnowPaintSavePatch),
            FixName,
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(Game), "Update"), typeof(FrameHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.PrepareSave)), typeof(WorldSaveHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(TerrainComp), "OnDestroy"), typeof(UnloadHook)));

        // Replaces `this.Save(paintOnly)` with `LimitedSave(this, paintOnly, modifier)`, and sends the
        // Poke and ResetGrass after it through helpers. All three or none.
        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("DoOperation")]
        private static IEnumerable<CodeInstruction> DoOperationTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original) {
            List<CodeInstruction> source = PatchHelper.Copy(instructions);

            List<CodeInstruction> codes = PatchHelper.Copy(source);
            int saves = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (!codes[i].Calls(SaveMethod)) { continue; }

                // The call becomes ldarg.3, the modifier (keeping any labels on it), and the static
                // call follows: stack [this, paintOnly] -> [.., modifier] -> LimitedSave(...).
                codes[i].opcode = OpCodes.Ldarg_3;
                codes[i].operand = null;
                codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, LimitedSaveMethod));
                saves++;
                i++;
            }

            if (saves != 1) {
                PatchHelper.WarnOnce($"{Site}: expected 1 Save call, found {saves}, so {FixName} is inactive here.");
                return source;
            }

            // ReplaceCalls hands back what it was given when the count is wrong, and logs why, so a
            // failure is told apart by the replacement it did not make.
            List<CodeInstruction> poked = PatchHelper.Copy(PatchHelper.ReplaceCalls(codes, PokeMethod, LimitedPokeMethod, Site, 1, original));
            if (!poked.Exists(c => c.Calls(LimitedPokeMethod))) { return source; }

            List<CodeInstruction> done = PatchHelper.Copy(PatchHelper.ReplaceCalls(poked, ResetGrassMethod, LimitedResetGrassMethod, Site, 1, original));
            return done.Exists(c => c.Calls(LimitedResetGrassMethod)) ? done : source;
        }

        private static void LimitedSave(TerrainComp comp, bool paintOnly, TerrainOp.Settings modifier) {
            _nothingVisible = false;

            // Turned off, a zone already waiting still saves on its turn in FrameHook.
            if (!paintOnly || modifier.m_pointMultiplicationFactor <= 0f || !IsOn || !Hooks.Healthy) {
                comp.Save(paintOnly);
                return;
            }

            Tile tile = TileFor(comp);
            int seen = VisibleHash(comp);
            if (tile.HasSeen && seen == tile.Seen) {
                _nothingVisible = true;
                return;
            }

            tile.HasSeen = true;
            tile.Seen = seen;

            float now = Time.realtimeSinceStartup;
            if (now >= tile.NextSave) {
                SaveTile(tile, now);
            } else if (!tile.Waiting) {
                ZDO zdo = comp.m_nview.GetZDO();
                tile.Zdo = zdo;
                tile.ZdoId = zdo.m_uid;
                tile.Waiting = true;
                WaitingTiles.Add(tile);
            }
        }

        private static void LimitedPoke(Heightmap hmap, int delayed, bool paintOnly) {
            if (!_nothingVisible) { hmap.Poke(delayed, paintOnly); }
        }

        private static void LimitedResetGrass(ClutterSystem clutter, Vector3 center, float radius) {
            if (!_nothingVisible) { clutter.ResetGrass(center, radius); }
        }

        private static bool IsOn => Enabled != null && Enabled.Value;

        private static Tile TileFor(TerrainComp comp) {
            int id = comp.GetInstanceID();
            if (!Tiles.TryGetValue(id, out Tile tile)) {
                tile = new Tile { Comp = comp };
                Tiles.Add(id, tile);
            }
            return tile;
        }

        private static void SaveTile(Tile tile, float now) {
            tile.Comp.Save(true);
            tile.NextSave = now + (SaveInterval != null ? Mathf.Max(SaveInterval.Value, 0f) : 5f);
            StopWaiting(tile);
        }

        private static void StopWaiting(Tile tile) {
            if (!tile.Waiting) { return; }

            tile.Waiting = false;
            tile.Zdo = null;
            WaitingTiles.Remove(tile);
        }

        // Every painted texel as the paint texture will hold it. Unity rounds to the nearest step
        // when it stores a Color in the RGBA32 texture Heightmap makes. A collision costs one missed
        // refresh, and the next step that changes puts it right.
        private static int VisibleHash(TerrainComp comp) {
            bool[] modified = comp.m_modifiedPaint;
            Color[] mask = comp.m_paintMask;

            int hash = 17;
            for (int i = 0; i < modified.Length; i++) {
                if (!modified[i]) { continue; }

                Color c = mask[i];
                int steps = Step(c.r) | (Step(c.g) << 8) | (Step(c.b) << 16) | (Step(c.a) << 24);
                hash = unchecked((hash * 31 + i) * 31 + steps);
            }
            return hash;
        }

        private static int Step(float value) => (int)(value * 255f + 0.5f);

        [HarmonyPatch(typeof(Game))]
        internal static class FrameHook {
            // Oldest first, and one a frame: a save is several milliseconds.
            [HarmonyPostfix]
            [HarmonyPatch("Update")]
            private static void UpdatePostfix() {
                if (WaitingTiles.Count == 0) { return; }

                float now = Time.realtimeSinceStartup;
                foreach (Tile tile in WaitingTiles) {
                    if (now < tile.NextSave) { continue; }

                    SaveTile(tile, now);
                    return;
                }
            }
        }

        [HarmonyPatch(typeof(ZDOMan))]
        internal static class WorldSaveHook {
            // On the server, before the world's ZDOs are copied for the save thread.
            [HarmonyPrefix]
            [HarmonyPatch(nameof(ZDOMan.PrepareSave))]
            private static void PrepareSavePrefix() {
                float now = Time.realtimeSinceStartup;
                while (WaitingTiles.Count > 0) {
                    SaveTile(WaitingTiles[WaitingTiles.Count - 1], now);
                }
            }
        }

        [HarmonyPatch(typeof(TerrainComp))]
        internal static class UnloadHook {
            [HarmonyPrefix]
            [HarmonyPatch("OnDestroy")]
            private static void OnDestroyPrefix(TerrainComp __instance) {
                int id = __instance.GetInstanceID();
                if (!Tiles.TryGetValue(id, out Tile tile)) { return; }

                Tiles.Remove(id);
                if (!tile.Waiting) { return; }

                ZDO zdo = tile.Zdo;
                ZDOID zdoId = tile.ZdoId;
                StopWaiting(tile);

                // A ZDO goes back to its pool when it is destroyed, so it may be another object's by now.
                ZNetView view = __instance.m_nview;
                if (ZDOMan.instance == null || ReferenceEquals(view, null) || zdo.m_uid != zdoId || ZDOMan.instance.GetZDO(zdoId) != zdo || !zdo.IsOwner()) { return; }

                if (view.IsValid()) {
                    __instance.Save(true);
                    return;
                }

                view.m_zdo = zdo;
                __instance.Save(true);
                view.m_zdo = null;
            }
        }
    }
}
