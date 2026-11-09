using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Grass Rebuild Burst: a whole-area grass rebuild is spread over a few frames, nearest
    // patches first.
    //
    // Grass generation is normally one patch per frame, but ClutterSystem.GeneratePatches'
    // rebuildAll path skips that limiter. TerrainComp.CheckLoad triggers it with a whole-zone
    // radius whenever a zone with saved terrain edits loads, regenerating up to ~64 patches at
    // hundreds of raycasts each in one frame: a reliable stutter entering built-up areas.
    //
    // A prefix replaces the unbudgeted sweep with one pass of vanilla's own ring walk that builds up
    // to Budget patches, nearest first: GeneratePatch's one-per-pass limiter is reopened after each
    // build until the budget is spent. It then re-arms m_forceRebuild so LateUpdate resumes next frame
    // until nothing is left. No placement logic is copied; every build is vanilla's. Patches beyond the
    // budget keep their old grass for a few frames.
    //
    // Client: ClutterSystem needs a camera.
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
                "How many grass patches a full rebuild may regenerate per frame. Higher finishes the " +
                "rebuild sooner but hitches more; 64 is effectively vanilla.",
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
