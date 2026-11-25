using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // FindGround takes raycast hits in PhysX's shifting order, so on stacked floors tile edges re-link each rebuild.
    // Transpiles Pathfinding.FindGround's RaycastNonAlloc call to sort hits nearest first, ties ordered by collider.
    // Creatures' navigation links on stacked floors stay put instead of being replaced every few seconds.
    // Provenance: the cause and the sort were found by Warp (issue #27).
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(Pathfinding))]
    internal static class PathfindingGroundOrderPatch {
        private const string FixName = "Fix Pathfinding Link Churn";

        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(PathfindingGroundOrderPatch),
                ValConfig.SectionCorrectness,
                FixName,
                true,
                "Keeps creatures' navigation links on closely stacked floors stable instead of replacing them " +
                "every few seconds; turning it on needs a game restart.");
        }

        private static readonly MethodInfo RaycastNonAllocMethod =
            AccessTools.Method(typeof(Physics), nameof(Physics.RaycastNonAlloc),
                new[] { typeof(Vector3), typeof(Vector3), typeof(RaycastHit[]), typeof(float), typeof(int) });
        private static readonly MethodInfo SortedRaycastMethod =
            AccessTools.Method(typeof(PathfindingGroundOrderPatch), nameof(SortedRaycastNonAlloc));

        // FindGround keeps each hit at least one agent height from the last one kept, and ConnectAlongEdge
        // compares its result by index, so hit order alone decides whether an edge's links are replaced.
        // Sorted top to bottom, the filter and water adjustment give what vanilla gives for in-order hits.
        private static int SortedRaycastNonAlloc(Vector3 origin, Vector3 direction, RaycastHit[] results, float maxDistance, int layerMask) {
            int count = Physics.RaycastNonAlloc(origin, direction, results, maxDistance, layerMask);
            if (Enabled.Value) { SortNearestFirst(results, count); }

            return count;
        }

        // Insertion sort: allocation-free, where Array.Sort with a comparer allocates on every call,
        // and fastest for the few hits a ray usually returns. Reads the collider's id, not the
        // collider, so a tie costs no engine lookup.
        internal static void SortNearestFirst(RaycastHit[] hits, int count) {
            for (int i = 1; i < count; i++) {
                RaycastHit hit = hits[i];
                float distance = hit.distance;
                int collider = hit.colliderInstanceID;

                int j = i - 1;
                while (j >= 0) {
                    float other = hits[j].distance;
                    if (other < distance || (other == distance && hits[j].colliderInstanceID <= collider)) { break; }

                    hits[j + 1] = hits[j];
                    j--;
                }

                hits[j + 1] = hit;
            }
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("FindGround")]
        private static IEnumerable<CodeInstruction> FindGroundTranspiler(IEnumerable<CodeInstruction> instructions) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            return PatchHelper.ReplaceCalls(instructions, RaycastNonAllocMethod, SortedRaycastMethod, "Pathfinding.FindGround", expected: 1);
        }
    }
}
