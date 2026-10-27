using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Pathfinding Link Churn: path-finding reads the ground under each tile edge top to bottom,
    // so floors that have not moved get the same navigation links on every rebuild.
    //
    // Pathfinding joins neighbouring tiles with links placed by FindGround, which casts one ray down
    // through everything at that spot and keeps each hit at least one agent height from the last one
    // kept. It walks the hits in the order PhysX returns them, which is unsorted and changes as nearby
    // colliders move. Where floors are closer together than the agent height, a different floor wins
    // from one rebuild to the next, and even the same floors can come back in a different list order.
    // ConnectAlongEdge compares that list by index, so either way it removes and re-adds every link on
    // the edge each time the tile rebuilds, every few seconds while creatures path there.
    //
    // A transpiler swaps FindGround's RaycastNonAlloc call for one that sorts the hits nearest first,
    // which for this downward ray is top to bottom, with equal distances ordered by collider. The
    // filter and the water adjustment run unchanged on the sorted hits, so the result is the one
    // vanilla gives whenever PhysX happens to return them in order.
    //
    // Both: path-finding runs wherever a creature's owner is, a dedicated server included.
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
                "Reads the ground under path-finding tile edges top to bottom, so creatures' navigation " +
                "links land on the same floor every time. Where floors are stacked closer than a creature " +
                "is tall, vanilla took whichever the physics engine listed first, which changes, and " +
                "replaced every link along that edge every few seconds. Changing this requires a game restart.");
        }

        private static readonly MethodInfo RaycastNonAllocMethod =
            AccessTools.Method(typeof(Physics), nameof(Physics.RaycastNonAlloc),
                new[] { typeof(Vector3), typeof(Vector3), typeof(RaycastHit[]), typeof(float), typeof(int) });
        private static readonly MethodInfo SortedRaycastMethod =
            AccessTools.Method(typeof(PathfindingGroundOrderPatch), nameof(SortedRaycastNonAlloc));

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
