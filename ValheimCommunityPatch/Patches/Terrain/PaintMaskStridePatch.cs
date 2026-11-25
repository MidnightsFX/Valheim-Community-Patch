using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Three paint methods walk the 33-wide paint array with a stride of 32, skewing paint and skipping the shared edge.
    // Prefixes Heightmap.SetPaintMask, TerrainComp.UpdatePaintMask and Heightmap.UpdateTerrainAlpha at the right stride.
    // The optterrain console command, their only caller, reads and writes terrain paint where it is, seams included.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class PaintMaskStridePatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(PaintMaskStridePatch),
                ValConfig.SectionTerrain,
                "Fix Terrain Paint Mask Indexing",
                true,
                "Makes the optterrain console command read and write terrain paint where it is, instead of skewed " +
                "diagonally and short of each zone's shared edge.");
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
            hmap.Poke();
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
