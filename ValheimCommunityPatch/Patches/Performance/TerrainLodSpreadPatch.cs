using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Distant Terrain Hitch: the far-terrain ring rebuilds a few tiles per frame instead of
    // all nine at once.
    //
    // Every 256 m of travel TerrainLod.RebuildAllHeightmaps regenerates its nine 81x81 distant
    // heightmaps in one frame, each with a per-vertex biome lookup on the main thread. That is a
    // reliable hitch on a fixed travel cadence, most noticeable while sailing.
    //
    // A prefix rebuilds at most Budget tiles per call and leaves the global state at NeedsRebuild
    // until the ring is complete, so UpdateHeightmaps re-enters next frame. A companion prefix on
    // IsTerrainReady reports a tile already rebuilt this cycle as ready; without it vanilla would
    // re-enqueue that tile on the build thread and rebuild it again. During the spread the ring is
    // briefly torn between the old and new centre, which under distance fog is far less visible
    // than the hitch. A budget of 9 is exactly vanilla.
    //
    // Client: the system needs a camera.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(TerrainLod))]
    internal static class TerrainLodSpreadPatch {
        internal static ConfigEntry<int> Budget;

        internal static void BindConfig() {
            Budget = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Distant Terrain Rebuild Budget",
                3,
                "How many of the nine distant-terrain tiles may rebuild per frame. Higher finishes " +
                "the ring sooner but hitches more; 9 is exactly vanilla.",
                advanced: true,
                valMin: 1,
                valMax: 9);
        }

        [HarmonyPrefix]
        [HarmonyPatch("RebuildAllHeightmaps")]
        private static bool RebuildAllHeightmapsPrefix(TerrainLod __instance) {
            int budget = Budget != null ? Budget.Value : 3;
            if (budget >= __instance.m_heightmaps.Count) { return true; }

            int rebuilt = 0;
            bool remaining = false;
            for (int i = 0; i < __instance.m_heightmaps.Count; i++) {
                TerrainLod.HeightmapWithOffset entry = __instance.m_heightmaps[i];
                if (entry.m_state == TerrainLod.HeightmapState.Done) { continue; }

                if (rebuilt >= budget) {
                    remaining = true;
                    break;
                }

                __instance.RebuildHeightmap(entry);
                rebuilt++;
            }

            if (!remaining) { __instance.m_heightmapState = TerrainLod.HeightmapState.Done; }

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch("IsTerrainReady", typeof(TerrainLod.HeightmapWithOffset))]
        private static bool IsTerrainReadyPrefix(TerrainLod.HeightmapWithOffset heightmapWithOffset, ref bool __result) {
            if (heightmapWithOffset.m_state == TerrainLod.HeightmapState.Done) {
                __result = true;
                return false;
            }

            return true;
        }
    }
}
