using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Snow paint never settles under Save's exact-float check, so every melt tick saves and resends a whole zone.
    // Transpiles TerrainComp.DoOperation so snow paint saves only on an 8-bit change, at most once per save interval.
    // Fires and footsteps in snow stop flooding the main thread and the network with whole-zone saves.
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
                "Saves and refreshes the ground for Deep North snow only when the change could be seen, at most " +
                "once per 'Snow Paint Save Interval', so fires in a base no longer drag the game down.");

            SaveInterval = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Snow Paint Save Interval",
                5f,
                "Seconds between snow saves of one zone's terrain with Fix Snow Paint Saves on; other players see " +
                "melting up to this much later. 0 saves every visible change.",
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

            // In the game only snow_fire, snow_shovel, snow_tree and snow_walk are paint-only with a
            // multiplication factor; hoe, cultivator, roads, level, raise and other mods' ops stay vanilla.
            // Turned off, a zone already waiting still saves on its turn in FrameHook.
            if (!paintOnly || modifier.m_pointMultiplicationFactor <= 0f || !IsOn || !Hooks.Healthy) {
                comp.Save(paintOnly);
                return;
            }

            // The factor fades the paint by a fraction of itself each time, so it never holds still
            // to vanilla's exact float hash. Nothing 8-bit changed means nothing can look different;
            // skipping the rebuild too keeps getMask reading the texture, so the fade settles.
            Tile tile = TileFor(comp);
            int seen = VisibleHash(comp);
            if (tile.HasSeen && seen == tile.Seen) {
                _nothingVisible = true;
                return;
            }

            tile.HasSeen = true;
            tile.Seen = seen;

            // The paint waits in memory, which is what the zone draws from anyway. At most one interval
            // can be lost, on a peer that disconnects or gives up the zone first; a fire melts it again.
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
            // On the server, before the world's ZDOs are copied for the save thread. Clients are not
            // told to flush, so a dedicated server's save can be up to one interval behind them.
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

                // ZNetScene has already reset the view, so the ZDO is lent back for this one save.
                view.m_zdo = zdo;
                __instance.Save(true);
                view.m_zdo = null;
            }
        }
    }
}
