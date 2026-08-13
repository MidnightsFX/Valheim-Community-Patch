using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Vanilla defect: three places walk the terrain paint mask with the wrong stride or bounds.
    //
    // A heightmap of m_width cells has (m_width + 1) vertices per axis, and every paint array is sized
    // (m_width + 1)^2 accordingly - TerrainComp.Initialize allocates `new Color[num * num]` with
    // `num = m_width + 1`, and Heightmap.Initialize creates the paint texture at that size. But:
    //
    //   TerrainComp.UpdatePaintMask:  index = y * m_width + x,  loops bounded by m_width
    //   Heightmap.UpdateTerrainAlpha: index = y * m_width + x,  loops bounded by m_width
    //
    // Indexing a 33-wide array with a stride of 32 slips one column further left on every row, so the
    // paint data is read and written along a diagonal skew rather than at the intended coordinates -
    // it is not a uniform offset, it worsens as y grows. Both loops also stop one short, so the final
    // row and column are never processed at all.
    //
    // And:
    //
    //   Heightmap.SetPaintMask: if (x < 0 || y < 0 || x >= m_width || y >= m_width) return;
    //
    // rejects index m_width, which is precisely the row and column a zone *shares with its
    // neighbour* - so terrain paint can never be written to the seam between two zones.
    //
    // Fix: correct the stride and the bounds in all three. These are only reachable from the
    // `updateterrainalpha` console path and TerrainComp.UpdatePaintMask, so the blast radius is small,
    // and each method is short enough that replacing it outright is clearer than an IL edit.
    [HarmonyPatch]
    internal static class PaintMaskStridePatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindServerConfig(
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
