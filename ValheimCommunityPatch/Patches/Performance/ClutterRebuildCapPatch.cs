using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Grass Rebuild Burst: loading a zone with terrain edits regenerates up to ~64 grass patches in one frame.
    // Prefixes ClutterSystem.GeneratePatches' rebuildAll path to build a few patches per frame in vanilla's ring order.
    // Entering built-up areas no longer stutters; the farther patches keep their old grass for a few frames.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(ClutterSystem))]
    internal static class ClutterRebuildCapPatch {
        internal static ConfigEntry<int> Budget;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(ClutterRebuildCapPatch));

        internal static void BindConfig() {
            Budget = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Grass Rebuild Budget",
                8,
                "Grass patches a full rebuild may regenerate per frame. Higher finishes sooner but hitches " +
                "more; 64 is effectively vanilla.",
                advanced: true,
                valMin: 1,
                valMax: 64);
        }

        [HarmonyPrefix]
        [HarmonyPatch("GeneratePatches")]
        private static bool GeneratePatchesPrefix(ClutterSystem __instance, bool rebuildAll, Vector3 center) {
            // Turned off with a rebuild under way, vanilla's next full rebuild finishes it at once.
            if (!rebuildAll || ApiSwitch.Off) { return true; }

            int budget = Budget != null ? Budget.Value : 8;

            // LateUpdate cleared m_forceRebuild before calling us; re-arm it if work may remain.
            if (RunRing(__instance, center, budget) >= budget) { __instance.m_forceRebuild = true; }

            return false;
        }

        // Vanilla's GeneratePatches ring walk with rebuildAll false, so GeneratePatch's own limiter
        // stops it building after one patch. Reopening that after each build, while the budget lasts,
        // builds the same nearest missing patches one full walk per build would, in the same order, for
        // one walk's cost. The walk still visits every slot, which is what keeps live patches from
        // timing out. Returns how many patches it built.
        private static int RunRing(ClutterSystem clutter, Vector3 center, int budget) {
            int built = 0;
            bool generated = false;

            Vector2Int vegPatch = clutter.GetVegPatch(center);
            Visit(vegPatch);
            int num = Mathf.CeilToInt((clutter.m_distance - clutter.m_grassPatchSize / 2f) / clutter.m_grassPatchSize);
            for (int index = 1; index <= num; ++index) {
                for (int x = vegPatch.x - index; x <= vegPatch.x + index; ++x) {
                    Visit(new Vector2Int(x, vegPatch.y - index));
                    Visit(new Vector2Int(x, vegPatch.y + index));
                }
                for (int y = vegPatch.y - index + 1; y <= vegPatch.y + index - 1; ++y) {
                    Visit(new Vector2Int(vegPatch.x - index, y));
                    Visit(new Vector2Int(vegPatch.x + index, y));
                }
            }

            return built;

            void Visit(Vector2Int patch) {
                bool before = generated;
                clutter.GeneratePatch(center, patch, ref generated, false);
                if (before || !generated) { return; }

                built++;
                if (built < budget) { generated = false; }
            }
        }
    }
}
