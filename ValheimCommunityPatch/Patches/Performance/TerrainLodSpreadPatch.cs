using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Distant Terrain Hitch: TerrainLod rebuilds all nine distant heightmaps in one frame every 256 m of travel.
    // A RebuildAllHeightmaps prefix rebuilds a few tiles per frame; an IsTerrainReady prefix keeps rebuilt ones ready.
    // Travel, sailing most of all, no longer hitches each time the far-terrain ring moves.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(TerrainLod))]
    internal static class TerrainLodSpreadPatch {
        private const string FixName = "Fix Distant Terrain Hitch";

        internal static ConfigEntry<int> Budget;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(TerrainLodSpreadPatch));

        // Both prefixes stand down together when another mod rewrites RebuildAllHeightmaps (ValheimOptimized
        // spreads the ring itself): two spreads of one ring would each rebuild tiles the other just did.
        // IsTerrainReady runs first in UpdateHeightmaps, so the check settles before any tile is marked.
        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            typeof(TerrainLodSpreadPatch),
            AccessTools.DeclaredMethod(typeof(TerrainLod), "RebuildAllHeightmaps"),
            transpilers: true,
            owners => $"Distant terrain rebuilding is changed by {owners}, so '{FixName}' stands down and " +
                      "that mod's pace applies.");

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

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches. While it spreads, the ring is briefly
        // torn between the old and new centre, far less visible under distance fog than the hitch.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("RebuildAllHeightmaps")]
        private static bool RebuildAllHeightmapsPrefix(TerrainLod __instance, bool __runOriginal) {
            if (ApiSwitch.Off || Takeover.TakenOver) { return true; }
            if (!__runOriginal) { return false; }

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

            // Left at NeedsRebuild until the ring is complete, so UpdateHeightmaps comes back next frame.
            if (!remaining) { __instance.m_heightmapState = TerrainLod.HeightmapState.Done; }

            return false;
        }

        // A tile already rebuilt this cycle is ready; vanilla would queue it on the build thread again.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("IsTerrainReady", typeof(TerrainLod.HeightmapWithOffset))]
        private static bool IsTerrainReadyPrefix(
            TerrainLod.HeightmapWithOffset heightmapWithOffset, ref bool __result, bool __runOriginal) {
            if (ApiSwitch.Off || Takeover.TakenOver) { return true; }
            if (!__runOriginal) { return false; }

            if (heightmapWithOffset.m_state == TerrainLod.HeightmapState.Done) {
                __result = true;
                return false;
            }

            return true;
        }
    }
}
