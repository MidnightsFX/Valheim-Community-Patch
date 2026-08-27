using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: every 256 m of travel, TerrainLod rebuilds its whole distant-terrain ring -
    // nine 81x81-vertex heightmaps - in a single frame (TerrainLod.RebuildAllHeightmaps,
    // TerrainLod.cs:79-84). Each rebuild runs the full Heightmap.Regenerate chain, including a
    // per-vertex WorldGenerator.GetBiome on the main thread in RebuildRenderMesh (distant-LOD
    // maps colour vertices by live biome lookup, Heightmap.cs:538). The result is a reliable
    // hitch on a fixed travel cadence - most noticeable sailing.
    //
    // Fix: rebuild at most N of the nine per frame (default 3). The state machine cooperates:
    // while any map is still pending, the global state stays NeedsRebuild, so UpdateHeightmaps
    // re-enters next frame, and NeedsRebuild() short-circuits on that state without re-targeting
    // m_lastPoint mid-cycle (TerrainLod.cs:119). One companion hook is load-bearing: vanilla's
    // per-map IsTerrainReady treats anything not ReadyToRebuild as "ask the build thread"
    // (TerrainLod.cs:101-106), and a map already rebuilt this cycle has had its build data
    // consumed - without the short-circuit below, IsAllTerrainReady would re-enqueue finished
    // maps on the build thread, flip them back to ReadyToRebuild when the redundant build lands,
    // and rebuild them again. Vanilla never evaluates a Done map mid-cycle (the global Done state
    // short-circuits first), so the hook is behaviour-neutral for vanilla flow.
    //
    // Trade-off, documented rather than hidden: during the spread (three frames at the default
    // budget) the 3x3 ring is positionally torn - rebuilt tiles sit at the new centre, pending
    // ones at the old. At 800+ metres under distance fog that is far less visible than the hitch;
    // the budget is configurable and 9 restores vanilla exactly.
    //
    // Client: the whole system is camera-driven (NeedsRebuild requires a main camera); nothing
    // headless ever rebuilds distant terrain.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(TerrainLod))]
    internal static class TerrainLodSpreadPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> Budget;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(TerrainLodSpreadPatch),
                ValConfig.SectionPerformance,
                "Fix Distant Terrain Hitch",
                true,
                "Spreads the distant-terrain rebuild that happens every 256m of travel over a few " +
                "frames instead of rebuilding all nine far-terrain tiles in one. Most noticeable " +
                "as the fixed-cadence hitch while sailing.");

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
            if (Enabled == null || !Enabled.Value) { return true; }

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

                // Vanilla's per-map rebuild: repositions to the new centre and Regenerates,
                // marking the map Done.
                __instance.RebuildHeightmap(entry);
                rebuilt++;
            }

            // Global Done only when the ring is complete; otherwise the state stays NeedsRebuild
            // and UpdateHeightmaps re-enters next frame.
            if (!remaining) { __instance.m_heightmapState = TerrainLod.HeightmapState.Done; }

            return false;
        }

        // A map rebuilt earlier in this cycle is ready by definition - see the header for why
        // letting vanilla ask the build thread about it causes redundant rebuilds.
        [HarmonyPrefix]
        [HarmonyPatch("IsTerrainReady", typeof(TerrainLod.HeightmapWithOffset))]
        private static bool IsTerrainReadyPrefix(TerrainLod.HeightmapWithOffset heightmapWithOffset, ref bool __result) {
            if (Enabled == null || !Enabled.Value) { return true; }

            if (heightmapWithOffset.m_state == TerrainLod.HeightmapState.Done) {
                __result = true;
                return false;
            }

            return true;
        }
    }
}
