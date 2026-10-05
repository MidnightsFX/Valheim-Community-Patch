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
    // A postfix on GetBiomeColor(ix, iy), the per-vertex corner lerp of near terrain, splits each vertex
    // between swamp, plains and a bridge strip where the two meet, keeping the vertex's total biome
    // weight; "Swamp Plains Blend Sharpness" sets how narrow the strip is. The strip's ground is chosen
    // by "Swamp Plains Bridge Ground":
    //  - BlackForest, the only earthy code with neither channel (Meadows reads as bright grass). A fixed
    //    dead zone keeps any triangle from holding one vertex with swamp and another with plains, so no
    //    pixel decodes as Ashlands. A BlackForest-to-plains blend decodes as part Mistlands, as every
    //    vanilla BlackForest and plains border already does.
    //  - Ashlands, deliberately, in both channels. Near water the shoreline then fills the strip and ends
    //    where the strip does, following the swamp and plains crossover instead of the zone grid. Its
    //    weight is capped below 0.9, above which the shader draws lava from the base mask's alpha, which
    //    is 1 everywhere outside the Ashlands. It applies only where the corners contributing
    // to the vertex include both a swamp and a plains code and no real Ashlands or Mistlands code (whose
    // alpha is genuine); on a shared edge only the two edge corners contribute, so both tiles decide
    // alike and meet without a seam. Distant terrain is left alone: the branch only runs within about
    // 400 m of the camera.
    //
    // Client: vertex colors are rendering state.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(Heightmap))]
    internal static class BiomeColorAliasPatch {
        internal enum BridgeGround { BlackForest, Ashlands }

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Sharpness;
        internal static ConfigEntry<BridgeGround> Bridge;

        // Across one triangle the corner lerp moves swamp-minus-plains by at most about 0.094, plus up
        // to about 0.03 of Color32 rounding, so vertices either side of a 2 x 0.1 dead band never meet.
        private const float DeadZone = 0.1f;

        // The shader starts drawing lava at an Ashlands weight of 0.9.
        private const float AshlandsCap = 0.85f;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(BiomeColorAliasPatch),
                ValConfig.SectionTerrain,
                "Fix Swamp Plains Shore Seams",
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

        // RebuildRenderMesh asks for every vertex of one tile in a row, so each tile is read once.
        private static Heightmap _cachedMap;
        private static BiomeSector[] _cachedCorners;
        private static int _swampCorners;
        private static int _plainsCorners;
        private static int _protectedCorners;

        [HarmonyPostfix]
        [HarmonyPatch("GetBiomeColor", typeof(float), typeof(float))]
        private static void GetBiomeColorPostfix(Heightmap __instance, float ix, float iy, ref Color __result) {
            if (Enabled == null || !Enabled.Value) { return; }

            // Neither channel set: the remap would return it unchanged.
            if (__result.r <= 0f && __result.a <= 0f) { return; }
            if (__instance.IsDistantLod) { return; }

            ReadCorners(__instance);

            // Corners 0-3 are SW, SE, NW, NE; a corner contributes unless its lerp weight is zero.
            int contributing = (ix < 1f && iy < 1f ? 1 : 0)
                             | (ix > 0f && iy < 1f ? 2 : 0)
                             | (ix < 1f && iy > 0f ? 4 : 0)
                             | (ix > 0f && iy > 0f ? 8 : 0);

            if ((contributing & _protectedCorners) != 0) { return; }
            if ((contributing & _swampCorners) == 0 || (contributing & _plainsCorners) == 0) { return; }

            float swampPlains = __result.r + __result.a;
            float d = __result.r - __result.a;

            // Each side ramps in outside the dead band; what they give up goes to the bridge.
            float sharpness = Sharpness != null ? Mathf.Max(1f, Sharpness.Value) : 1f;
            float ramp = (1f - DeadZone) / sharpness;
            float swamp = Mathf.Clamp((d - DeadZone) / ramp, 0f, swampPlains);
            float plains = Mathf.Clamp((-d - DeadZone) / ramp, 0f, swampPlains);
            float bridge = swampPlains - swamp - plains;

            if (Bridge != null && Bridge.Value == BridgeGround.Ashlands) {
                // Ashlands' code is both channels at once. Past the cap the leftover reads as meadows.
                float ash = Mathf.Min(bridge, AshlandsCap);
                __result.r = swamp + ash;
                __result.a = plains + ash;
                return;
            }

            __result.r = swamp;
            __result.a = plains;
            __result.b += bridge;
        }

        private static void ReadCorners(Heightmap hmap) {
            BiomeSector[] corners = hmap.m_cornerBiomes;
            if (ReferenceEquals(hmap, _cachedMap) && ReferenceEquals(corners, _cachedCorners)) { return; }

            _cachedMap = hmap;
            _cachedCorners = corners;
            _swampCorners = 0;
            _plainsCorners = 0;
            _protectedCorners = 0;

            for (int i = 0; i < corners.Length && i < 4; i++) {
                if (corners[i] == null) { continue; }

                // The same code the vertex color is lerped from, terrain texture overrides included.
                Color32 code = Heightmap.GetBiomeColor(corners[i]);
                int bit = 1 << i;

                if (code.a > 0 && (code.r > 0 || code.b > 0)) {
                    _protectedCorners |= bit;
                } else {
                    if (code.r > 0) { _swampCorners |= bit; }
                    if (code.a > 0) { _plainsCorners |= bit; }
                }
            }
        }

        // Vertex colors are baked when a tile rebuilds, so a toggle only shows once the loaded tiles
        // are rebuilt; a delayed poke queues each for a full rebuild next LateUpdate.
        private static void RebuildLoadedTerrain() {
            if (RunMode.IsDedicated) { return; }

            _cachedMap = null;
            _cachedCorners = null;

            foreach (Heightmap hmap in Heightmap.s_heightmaps) {
                if (hmap != null) { hmap.Poke(1); }
            }
        }
    }
}
