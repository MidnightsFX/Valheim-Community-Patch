using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Fix Terrain Paint Mask Indexing: corrects the stride and bounds three methods use to walk
    // the terrain paint data.
    //
    // A heightmap of m_width cells has m_width + 1 vertices per axis, and every paint array is
    // sized to match. TerrainComp.UpdatePaintMask and Heightmap.UpdateTerrainAlpha index that
    // array with a stride of m_width and stop one short, so the paint is read along a diagonal
    // skew and the last row and column are never processed. Heightmap.SetPaintMask rejects index
    // m_width, which is the row and column a zone shares with its neighbour, so paint can never
    // be written to a seam.
    //
    // Prefixes replace all three with the same logic at the correct stride and bounds. All three
    // are only reachable from the optterrain console command.
    //
    // Client: UpdateTerrainAlpha returns immediately without a local player.
    [PatchSide(Side.Client)]
    [HarmonyPatch]
    internal static class PaintMaskStridePatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(PaintMaskStridePatch),
                ValConfig.SectionTerrain,
                "Fix Terrain Paint Mask Indexing",
                true,
                "Corrects the array stride and bounds used when reading and writing terrain paint. " +
                "Vanilla indexes a 33-wide paint array with a stride of 32, which skews the paint " +
                "diagonally, and refuses to write the row and column each zone shares with its neighbour.");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Heightmap), "SetPaintMask")]
        private static bool SetPaintMaskPrefix(Heightmap __instance, int x, int y, Color paint) {
            if (Enabled == null || !Enabled.Value) { return true; }

            int stride = __instance.m_width + 1;
            if (x < 0 || y < 0 || x >= stride || y >= stride) { return false; }

            __instance.m_paintMask.SetPixel(x, y, paint);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TerrainComp), nameof(TerrainComp.UpdatePaintMask))]
        private static bool UpdatePaintMaskPrefix(TerrainComp __instance, Heightmap hmap) {
            if (Enabled == null || !Enabled.Value) { return true; }
            if (!__instance.m_initialized) { return false; }

            int stride = __instance.m_width + 1;
            for (int y = 0; y < stride; y++) {
                for (int x = 0; x < stride; x++) {
                    int index = y * stride + x;
                    if (!__instance.m_modifiedPaint[index]) { continue; }

                    Color color = __instance.m_paintMask[index];
                    color.a = hmap.GetPaintMask(x, y).a;
                    __instance.m_paintMask[index] = color;
                }
            }

            __instance.Save();
            hmap.Poke(false);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.UpdateTerrainAlpha), new[] { typeof(Heightmap) })]
        private static bool UpdateTerrainAlphaPrefix(Heightmap hmap, ref bool __result) {
            if (Enabled == null || !Enabled.Value) { return true; }

            HeightmapBuilder.HMBuildData buildData = HeightmapBuilder.instance.RequestTerrainSync(
                hmap.transform.position, hmap.m_width, hmap.m_scale, hmap.IsDistantLod, WorldGenerator.instance);

            int stride = hmap.m_width + 1;
            int changed = 0;

            for (int y = 0; y < stride; y++) {
                for (int x = 0; x < stride; x++) {
                    float alpha = buildData.m_baseMask[y * stride + x].a;

                    Color paintMask = hmap.GetPaintMask(x, y);
                    if (alpha == paintMask.a) { continue; }

                    paintMask.a = alpha;
                    hmap.SetPaintMask(x, y, paintMask);
                    changed++;
                }
            }

            if (changed > 0) {
                hmap.GetAndCreateTerrainCompiler().UpdatePaintMask(hmap);
                Logger.LogInfo($"Corrected {changed} terrain alpha pixel(s) at {hmap.transform.position}.");
            }

            __result = changed > 0;
            return false;
        }
    }
}
