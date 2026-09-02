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
    // A prefix replaces the unbudgeted sweep with vanilla's own one-patch-per-pass ring walk, run
    // up to Budget times, then re-arms m_forceRebuild so LateUpdate resumes next frame until
    // nothing is left. No placement logic is copied; each pass is vanilla's. Patches beyond the
    // budget keep their old grass for a few frames.
    //
    // Client: ClutterSystem needs a camera.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(ClutterSystem))]
    internal static class ClutterRebuildCapPatch {
        internal static ConfigEntry<int> Budget;

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
            if (!rebuildAll) { return true; }

            int budget = Budget != null ? Budget.Value : 8;
            bool lastPassGenerated = false;

            for (int pass = 0; pass < budget; pass++) {
                bool generated = false;
                RunRing(__instance, center, ref generated);

                lastPassGenerated = generated;
                if (!generated) { break; }
            }

            // LateUpdate cleared m_forceRebuild before calling us; re-arm it if work remains.
            if (lastPassGenerated) { __instance.m_forceRebuild = true; }

            return false;
        }

        // Vanilla's GeneratePatches ring walk with rebuildAll false, so GeneratePatch's own
        // one-per-pass limiter applies. Each full pass regenerates the nearest missing patch.
        private static void RunRing(ClutterSystem clutter, Vector3 center, ref bool generated) {
            Vector2Int vegPatch = clutter.GetVegPatch(center);
            clutter.GeneratePatch(center, vegPatch, ref generated, false);
            int num = Mathf.CeilToInt((clutter.m_distance - clutter.m_grassPatchSize / 2f) / clutter.m_grassPatchSize);
            for (int index = 1; index <= num; ++index) {
                for (int x = vegPatch.x - index; x <= vegPatch.x + index; ++x) {
                    clutter.GeneratePatch(center, new Vector2Int(x, vegPatch.y - index), ref generated, false);
                    clutter.GeneratePatch(center, new Vector2Int(x, vegPatch.y + index), ref generated, false);
                }
                for (int y = vegPatch.y - index + 1; y <= vegPatch.y + index - 1; ++y) {
                    clutter.GeneratePatch(center, new Vector2Int(vegPatch.x - index, y), ref generated, false);
                    clutter.GeneratePatch(center, new Vector2Int(vegPatch.x + index, y), ref generated, false);
                }
            }
        }
    }
}
