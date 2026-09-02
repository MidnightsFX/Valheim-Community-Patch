using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: grass generation is normally budgeted to one patch per frame - GeneratePatch
    // bails once anything was generated (ClutterSystem.cs:154) - but the rebuildAll path skips that
    // limiter entirely. rebuildAll is fired by ClearAll (graphics settings) and, the case that
    // matters, by TerrainComp.CheckLoad -> ResetGrass with a whole-zone radius whenever a zone with
    // saved terrain edits loads (TerrainComp.cs:125). That regenerates every marked patch in range
    // in a single frame: up to ~64 patches, each of which raycasts per clutter instance in
    // GenerateVegPatch - hundreds of raycasts per patch. It is a reliable frame spike on entering
    // any built-up area.
    //
    // Fix: budget the rebuildAll path. Instead of one unbudgeted sweep, run vanilla's own
    // one-patch-per-pass ring walk (GeneratePatch with rebuildAll off) up to N times, which
    // regenerates the N nearest missing or reset patches in vanilla's own center-out order, then
    // re-arm m_forceRebuild so LateUpdate resumes next frame until nothing is left. No placement
    // logic is copied - each pass is vanilla's, so what gets generated and how cannot drift.
    //
    // The patches beyond the budget keep their old grass until their turn (or time out after 2 s,
    // exactly as any out-of-range patch does) - worst case a few frames of stale grass where the
    // spike used to be. The menu is unaffected: m_menuHack disables the per-pass limiter, so the
    // first pass generates everything and the second finds nothing, same as vanilla.
    //
    // Client: ClutterSystem requires a main camera; nothing headless ever generates grass.
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
            // The steady-state path is already budgeted by vanilla; only the burst needs taming.
            if (!rebuildAll) { return true; }

            int budget = Budget != null ? Budget.Value : 8;
            bool lastPassGenerated = false;

            for (int pass = 0; pass < budget; pass++) {
                bool generated = false;
                RunRing(__instance, center, ref generated);

                lastPassGenerated = generated;
                if (!generated) { break; }
            }

            // The budget ran out with work still being found: LateUpdate cleared m_forceRebuild
            // before calling us (ClutterSystem.cs:77), so re-arm it to resume next frame. Worst
            // case - the last pass generated the final patch - is one extra pass that finds
            // nothing and disarms.
            if (lastPassGenerated) { __instance.m_forceRebuild = true; }

            return false;
        }

        // Vanilla's GeneratePatches ring walk verbatim (ClutterSystem.cs:122-141), passing
        // rebuildAll: false so GeneratePatch's own one-per-pass limiter applies. Each full pass
        // over the ring regenerates exactly the nearest missing or reset patch; already-current
        // patches are a dictionary hit and a timer reset, same as vanilla visits them.
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
