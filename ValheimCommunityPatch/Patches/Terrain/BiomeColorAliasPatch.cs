using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Fix Swamp Plains Shore Seams: shores where swamp blends into plains no longer show the Ashlands
    // shoreline texture ending in hard straight lines along the 64 m zone grid.
    //
    // A terrain tile's vertex colors carry its biome as a color code per corner
    // (Heightmap.GetBiomeColor), lerped across the tile. The 1.0 terrain shader decodes each biome's
    // weight as one minus the largest per-channel distance to its code, and Ashlands' code (1,0,0,1)
    // is Swamp's (1,0,0,0) plus Plains' (0,0,0,1), so any swamp-to-plains blend also decodes as part
    // Ashlands. The shader's Ashlands branch runs whenever that weight is above zero rather than in
    // proportion to it, and paints flat ground within 2 m of the water with the Ashlands shoreline at
    // full strength. A tile that is all swamp or all plains has exactly zero Ashlands weight, so the
    // effect starts dead on the zone border. No color can mean "part swamp, part plains" without it.
    //
    // Each vertex of near terrain is split between swamp, plains and a bridge strip where the two meet,
    // keeping the vertex's total biome weight; "Swamp Plains Blend Sharpness" sets how narrow the strip
    // is. The strip's ground is chosen by "Swamp Plains Bridge Ground":
    //  - BlackForest, the only earthy code with neither channel (Meadows reads as bright grass). A fixed
    //    dead zone keeps any triangle from holding one vertex with swamp and another with plains, so no
    //    pixel decodes as Ashlands. A BlackForest-to-plains blend decodes as part Mistlands and shines;
    //    with Fix Plains Border Shine on, that fix runs this remap together with its own and the strip
    //    gives way to meadows before the plains (see PlainsBorderShinePatch).
    //  - Ashlands, deliberately, in both channels. Near water the shoreline then fills the strip and ends
    //    where the strip does, following the swamp and plains crossover instead of the zone grid. Its
    //    weight is capped below 0.9, above which the shader draws lava from the base mask's alpha, which
    //    is 1 everywhere outside the Ashlands. It applies only where the corners contributing
    // to the vertex include both a swamp and a plains code and no real Ashlands or Mistlands code (whose
    // alpha is genuine); on a shared edge only the two edge corners contribute, so both tiles decide
    // alike and meet without a seam. Distant terrain is left alone: the branch only runs within about
    // 400 m of the camera.
    //
    // RebuildRenderMesh colors each vertex through GetBiomeColor(ix, iy), the corner lerp. A transpiler
    // swaps that call for a pass shared with Fix Plains Border Shine, which reads the tile's corner codes
    // once and lerps and remaps every vertex itself, with no hooked call per vertex. A postfix on
    // GetBiomeColor(ix, iy) runs the same remap for other mods that call it, such as Seasonality
    // recoloring tiles each season. While another mod hooks that method, as Expand World Data does for
    // territory colors, the pass asks it for every vertex instead.
    //
    // Stands down when another mod can skip RebuildRenderMesh with a prefix, because that mod can
    // color the mesh without this lerp. ValheimOptimized does for all but a zone's first mesh near the
    // player or behind a loading screen, so a fixed shore lost the fix at its next rebuild.
    //
    // Client: vertex colors are rendering state.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Heightmap))]
    internal static class BiomeColorAliasPatch {
        private const string FixName = "Fix Swamp Plains Shore Seams";

        internal enum BridgeGround { BlackForest, Ashlands }

        internal static FixToggle Enabled;
        internal static ConfigEntry<float> Sharpness;
        internal static ConfigEntry<BridgeGround> Bridge;

        // Across one triangle the corner lerp moves swamp-minus-plains by at most about 0.094, plus up
        // to about 0.03 of Color32 rounding, so vertices either side of a 2 x 0.1 dead band never meet.
        internal const float DeadZone = 0.1f;

        // The shader starts drawing lava at an Ashlands weight of 0.9.
        internal const float AshlandsCap = 0.85f;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(BiomeColorAliasPatch),
                ValConfig.SectionTerrain,
                FixName,
                true,
                "Stops shores where swamp blends into plains from being drawn with the Ashlands shoreline, " +
                "which ends in hard straight lines along the 64m zone grid. The game's terrain colors " +
                "cannot express a swamp and plains blend without also meaning Ashlands, so this bridges " +
                "the two with a strip of ground where they meet instead (see Swamp Plains Bridge Ground).");

            Sharpness = ValConfig.BindServerConfig(
                ValConfig.SectionTerrain,
                "Swamp Plains Blend Sharpness",
                3f,
                "With Fix Swamp Plains Shore Seams on, how quickly swamp and plains ground give way to each " +
                "other. 1 blends across the whole 64m tile with a wide bridge strip in the middle; higher " +
                "values keep swamp and plains ground closer to the crossover and narrow the strip (about " +
                "11m at 3, 9m at 4). A black forest strip never gets narrower than about 5m.",
                advanced: true,
                valMin: 1f,
                valMax: 8f);

            Bridge = ValConfig.BindServerConfig(
                ValConfig.SectionTerrain,
                "Swamp Plains Bridge Ground",
                BridgeGround.BlackForest,
                "With Fix Swamp Plains Shore Seams on, the ground drawn in the strip where swamp meets plains. " +
                "BlackForest keeps the Ashlands look out entirely. Ashlands is closest to how the game " +
                "blends them: near water the Ashlands shoreline fills the strip and ends at its edge, " +
                "following where swamp and plains meet rather than the zone grid.",
                advanced: true);

            Enabled.SettingChanged += (sender, args) => RebuildLoadedTerrain();
            Sharpness.SettingChanged += (sender, args) => RebuildLoadedTerrain();
            Bridge.SettingChanged += (sender, args) => RebuildLoadedTerrain();
        }

        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            typeof(BiomeColorAliasPatch),
            AccessTools.DeclaredMethod(typeof(Heightmap), "RebuildRenderMesh"),
            transpilers: false,
            owners => $"Terrain mesh building is changed by {owners}, so '{FixName}' stands down and that " +
                      "mod's terrain colors apply.");

        // Callers ask for every vertex of one tile in a row, so each tile is read once; the tile pass
        // refreshes this for every tile it builds.
        private static Heightmap _cachedMap;
        private static BiomeSector[] _cachedCorners;

        // Bit i set when corner i (SW, SE, NW, NE) has that code; shared with PlainsBorderShinePatch.
        internal static int SwampCorners;
        internal static int ForestCorners;
        internal static int PlainsCorners;
        internal static int ProtectedCorners;

        internal static float SharpnessValue => Sharpness != null ? Mathf.Max(1f, Sharpness.Value) : 1f;
        internal static BridgeGround BridgeValue => Bridge != null ? Bridge.Value : BridgeGround.BlackForest;

        [HarmonyPostfix]
        [HarmonyPatch("GetBiomeColor", typeof(float), typeof(float))]
        private static void GetBiomeColorPostfix(Heightmap __instance, float ix, float iy, ref Color __result) {
            if (Enabled == null || !Enabled.Value) { return; }

            // Fix Plains Border Shine does this remap together with its own.
            if (PlainsBorderShinePatch.Handles) { return; }

            // Neither channel set: the remap would return it unchanged.
            if (__result.r <= 0f && __result.a <= 0f) { return; }
            if (__instance.IsDistantLod || Takeover.TakenOver) { return; }

            ReadCorners(__instance);
            Remap(ref __result, ContributingCorners(ix, iy), SharpnessValue, BridgeValue);
        }

        // The tile pass: RebuildRenderMesh's per-vertex `(Color32)this.GetBiomeColor(ix, iy)` becomes
        // VertexColor(this, ix, iy), and BeginTile(this) runs first in the method body, after every
        // prefix, so it reads the corners as the game's per-vertex calls would.

        private static readonly MethodInfo GameColorMethod =
            AccessTools.DeclaredMethod(typeof(Heightmap), "GetBiomeColor", new[] { typeof(float), typeof(float) });
        private static readonly MethodInfo ToColor32Method =
            AccessTools.Method(typeof(Color32), "op_Implicit", new[] { typeof(Color) });
        private static readonly MethodInfo BeginTileMethod =
            AccessTools.Method(typeof(BiomeColorAliasPatch), nameof(BeginTile));
        private static readonly MethodInfo VertexColorMethod =
            AccessTools.Method(typeof(BiomeColorAliasPatch), nameof(VertexColor));

        // The game's method with every mod's hooks on it, these postfixes included.
        private static readonly Func<Heightmap, float, float, Color> GameColor = GameColorMethod == null
            ? null
            : AccessTools.MethodDelegate<Func<Heightmap, float, float, Color>>(GameColorMethod);

        private const int NoRemap = 0;
        private const int SwampRemap = 1;
        private const int ShineRemap = 2;

        // The tile being rebuilt, as BeginTile found it.
        private static Heightmap _tile;
        private static bool _askGame = true;
        private static bool _oneCorner;
        private static Color32 _sw, _se, _nw, _ne;
        private static int _remap;
        private static bool _swampOn;
        private static float _swampSharpness;
        private static float _shineSharpness;
        private static BridgeGround _bridge;

        private static string _loggedHookOwners;

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches. Without the pass, the game's method
        // and the postfixes color every vertex as before.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("RebuildRenderMesh")]
        private static IEnumerable<CodeInstruction> RebuildRenderMeshTranspiler(IEnumerable<CodeInstruction> instructions) {
            const string site = "Heightmap.RebuildRenderMesh";
            if (GameColorMethod == null || GameColor == null || ToColor32Method == null
                || BeginTileMethod == null || VertexColorMethod == null) {
                PatchHelper.WarnOnce($"{site}: a method the terrain color pass needs could not be resolved, so " +
                                     "terrain colors are worked out per vertex as before.");
                return instructions;
            }

            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            int replaced = 0;
            for (int i = 0; i + 1 < codes.Count; i++) {
                if (!codes[i].Calls(GameColorMethod) || !codes[i + 1].Calls(ToColor32Method)) { continue; }

                // A jump to the conversion alone would skip the new call.
                if (codes[i + 1].labels.Count > 0 || codes[i + 1].blocks.Count > 0) { replaced = -1; break; }

                // Stack [this, ix, iy] -> VertexColor(this, ix, iy), already a Color32.
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = VertexColorMethod;
                codes.RemoveAt(i + 1);
                replaced++;
            }

            if (replaced != 1) {
                PatchHelper.WarnOnce(
                    $"{site}: expected 1 per-vertex GetBiomeColor call, found {(replaced < 0 ? "one this pass cannot rewrite" : replaced.ToString())}, " +
                    "so terrain colors are worked out per vertex as before. Another mod has most likely already " +
                    "rewritten the method - if so, nothing is wrong.");
                return instructions;
            }

            // BeginTile(this) ahead of the first instruction, taking over anything that jumps to it.
            CodeInstruction first = codes[0];
            codes.Insert(0, new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(first).MoveBlocksFrom(first));
            codes.Insert(1, new CodeInstruction(OpCodes.Call, BeginTileMethod));
            return codes;
        }

        // Reads the four corner codes and decides, as the two postfixes would for each vertex, which
        // remap this tile can need. Anything unexpected leaves the tile to the game's method.
        private static void BeginTile(Heightmap hmap) {
            _tile = hmap;
            _askGame = true;
            if (hmap.IsDistantLod) { return; }

            try {
                if (GameColorHooked()) { return; }

                BiomeSector[] corners = hmap.m_cornerBiomes;
                if (corners == null || corners.Length < 4
                    || corners[0] == null || corners[1] == null || corners[2] == null || corners[3] == null) {
                    return;
                }

                // Read fresh for every rebuild: a mod can change a biome's code while the corners stay,
                // as Seasonality does each season. The postfix's corner flags are refreshed with them.
                _cachedMap = null;
                ClearCorners();
                _sw = Heightmap.GetBiomeColor(corners[0]);
                _se = Heightmap.GetBiomeColor(corners[1]);
                _nw = Heightmap.GetBiomeColor(corners[2]);
                _ne = Heightmap.GetBiomeColor(corners[3]);
                AddCorner(0, _sw);
                AddCorner(1, _se);
                AddCorner(2, _nw);
                AddCorner(3, _ne);
                _cachedMap = hmap;
                _cachedCorners = corners;

                // The game's own shortcut: one sector at every corner (BiomeSector compares by reference).
                _oneCorner = corners[0] == corners[1] && corners[0] == corners[2] && corners[0] == corners[3];

                _swampOn = Enabled != null && Enabled.Value;
                _swampSharpness = SharpnessValue;
                _bridge = BridgeValue;
                _shineSharpness = PlainsBorderShinePatch.SharpnessValue;
                _remap = TileRemap();
                _askGame = false;
            } catch (Exception ex) {
                _cachedMap = null;
                PatchHelper.WarnOnce($"The terrain color pass could not read a terrain tile, so its colors are worked " +
                                     $"out per vertex as before: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // The postfixes' own conditions, for the corners this tile has. A remap a tile cannot need would
        // leave every one of its vertices unchanged.
        private static int TileRemap() {
            // Fix Plains Border Shine runs both remaps while it is on, and this fix's own stands aside.
            if (PlainsBorderShinePatch.Handles) {
                bool forestSide = ForestCorners != 0 || (_swampOn && SwampCorners != 0);
                return PlainsCorners != 0 && forestSide ? ShineRemap : NoRemap;
            }

            if (!_swampOn || SwampCorners == 0 || PlainsCorners == 0 || Takeover.TakenOver) { return NoRemap; }

            return SwampRemap;
        }

        // Exactly what the game's method and the postfixes give, without either call per vertex.
        private static Color32 VertexColor(Heightmap hmap, float ix, float iy) {
            if (_askGame || !ReferenceEquals(hmap, _tile)) { return GameColor(hmap, ix, iy); }

            Color32 code = _oneCorner
                ? _sw
                : Color32.Lerp(Color32.Lerp(_sw, _se, ix), Color32.Lerp(_nw, _ne, ix), iy);
            if (_remap == NoRemap) { return code; }

            // The game's method returns a Color, which the postfixes remap; a code converts back unchanged.
            Color color = code;
            if (_remap == ShineRemap) {
                if (color.r <= 0f && color.b <= 0f && color.a <= 0f) { return code; }

                PlainsBorderShinePatch.Remap(ref color, ContributingCorners(ix, iy), _shineSharpness,
                    _swampOn, _swampSharpness, _bridge);
            } else {
                if (color.r <= 0f && color.a <= 0f) { return code; }

                Remap(ref color, ContributingCorners(ix, iy), _swampSharpness, _bridge);
            }

            return color;
        }

        // Another mod's hook on the per-vertex color, which Expand World Data can add after the world
        // loads, so this is asked for every tile.
        private static bool GameColorHooked() {
            // Fully qualified: HarmonyLib.Patches collides with this mod's Patches namespace.
            HarmonyLib.Patches info = Harmony.GetPatchInfo(GameColorMethod);
            if (info == null) { return false; }

            SortedSet<string> owners = null;
            AddForeign(ref owners, info.Prefixes);
            AddForeign(ref owners, info.Postfixes);
            AddForeign(ref owners, info.Transpilers);
            AddForeign(ref owners, info.Finalizers);
            if (owners == null) { return false; }

            string names = string.Join(", ", owners);
            if (names != _loggedHookOwners) {
                _loggedHookOwners = names;
                Logger.LogInfo($"Terrain colors are changed by {names}, so terrain tiles ask the game for each " +
                               "vertex's color, with that mod's change and the terrain color fixes on top.");
            }

            return true;
        }

        private static void AddForeign(ref SortedSet<string> owners, IReadOnlyList<Patch> patches) {
            foreach (Patch patch in patches) {
                if (patch.owner == ValheimCommunityPatch.PluginGUID) { continue; }

                owners = owners ?? new SortedSet<string>(StringComparer.Ordinal);
                owners.Add(patch.owner);
            }
        }

        internal static void Remap(ref Color color, int contributing, float sharpness, BridgeGround ground) {
            if ((contributing & ProtectedCorners) != 0) { return; }
            if ((contributing & SwampCorners) == 0 || (contributing & PlainsCorners) == 0) { return; }

            float swampPlains = color.r + color.a;
            float d = color.r - color.a;

            // Each side ramps in outside the dead band; what they give up goes to the bridge.
            float ramp = (1f - DeadZone) / sharpness;
            float swamp = Mathf.Clamp((d - DeadZone) / ramp, 0f, swampPlains);
            float plains = Mathf.Clamp((-d - DeadZone) / ramp, 0f, swampPlains);
            float bridge = swampPlains - swamp - plains;

            if (ground == BridgeGround.Ashlands) {
                // Ashlands' code is both channels at once. Past the cap the leftover reads as meadows.
                float ash = Mathf.Min(bridge, AshlandsCap);
                color.r = swamp + ash;
                color.a = plains + ash;
                return;
            }

            color.r = swamp;
            color.a = plains;
            color.b += bridge;
        }

        // Corners 0-3 are SW, SE, NW, NE; a corner contributes unless its lerp weight is zero.
        internal static int ContributingCorners(float ix, float iy) {
            return (ix < 1f && iy < 1f ? 1 : 0)
                 | (ix > 0f && iy < 1f ? 2 : 0)
                 | (ix < 1f && iy > 0f ? 4 : 0)
                 | (ix > 0f && iy > 0f ? 8 : 0);
        }

        internal static void ReadCorners(Heightmap hmap) {
            BiomeSector[] corners = hmap.m_cornerBiomes;
            if (ReferenceEquals(hmap, _cachedMap) && ReferenceEquals(corners, _cachedCorners)) { return; }

            _cachedMap = hmap;
            _cachedCorners = corners;
            ClearCorners();

            for (int i = 0; i < corners.Length && i < 4; i++) {
                // The same code the vertex color is lerped from, terrain texture overrides included.
                if (corners[i] != null) { AddCorner(i, Heightmap.GetBiomeColor(corners[i])); }
            }
        }

        internal static void ClearCorners() {
            SwampCorners = 0;
            ForestCorners = 0;
            PlainsCorners = 0;
            ProtectedCorners = 0;
        }

        internal static void AddCorner(int index, Color32 code) {
            int bit = 1 << index;

            if (code.a > 0 && (code.r > 0 || code.b > 0)) {
                ProtectedCorners |= bit;
            } else {
                if (code.r > 0) { SwampCorners |= bit; }
                if (code.b > 0) { ForestCorners |= bit; }
                if (code.a > 0) { PlainsCorners |= bit; }
            }
        }

        // Vertex colors are baked when a tile rebuilds, so a toggle only shows once the loaded tiles
        // are rebuilt; a delayed poke queues each for a full rebuild next LateUpdate.
        internal static void RebuildLoadedTerrain() {
            if (RunMode.IsDedicated) { return; }

            _cachedMap = null;
            _cachedCorners = null;

            foreach (Heightmap hmap in Heightmap.s_heightmaps) {
                if (hmap != null) { hmap.Poke(1); }
            }
        }
    }
}
