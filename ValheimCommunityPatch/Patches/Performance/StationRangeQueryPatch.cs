using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Station Range Scans: the per-frame build-mode station and extension range queries
    // compare squared distances and read each candidate's position once instead of twice.
    //
    // Three static scans run at render framerate while building. Hud.SetupPieceInfo calls
    // CraftingStation.HaveBuildStationInRange every rendered frame, and Hud.UpdatePieceBuildStatus
    // calls it again for one icon per frame while the selection window is open.
    // Player.UpdatePlacementGhost, from Player.LateUpdate, calls both
    // StationExtension.FindClosestStationInRange and StationExtension.OtherExtensionInRange every
    // frame while a station-extension ghost is up. All three walk their whole static list with a
    // Vector3.Distance per element, which is a square root each. HaveBuildStationInRange also
    // reads allStation.transform.position twice per name-matching station - once to flatten the
    // query point's y onto it, once inside the distance call - and OtherExtensionInRange re-reads
    // its own transform.position inside the loop body, once per element.
    //
    // Prefixes replace all three with vanilla's own loop shape over an indexed list, hoisting the
    // position reads and comparing squared distances against a squared radius. In
    // HaveBuildStationInRange the y term drops out exactly, not approximately: vanilla assigns
    // point.y = allStation.transform.position.y before measuring, so dy is bit-exactly zero and
    // the sum under vanilla's root is bit-identical to the squared compare. Crucially
    // GetStationBuildRange is still called for every name-matching station in vanilla's order,
    // because it drives GetExtensions, which on its own two-second timer rebuilds the attached
    // extension list, recomputes the build range and resizes both the area marker and the effect
    // area collider; vanilla's early return on the first hit means later stations were never
    // pumped anyway, and that is preserved. Two sub-ULP boundary shifts follow from comparing
    // squares and are stated rather than hidden: a candidate whose true distance rounds up to
    // exactly the radius now counts as in range where vanilla counted it out, a band about
    // 3e-7 times the radius wide - roughly six microns at a twenty metre build range, against
    // world coordinates whose own float precision at five kilometres is half a millimetre - and
    // FindClosestStationInRange now breaks ties on the exact squared distance rather than the
    // rounded one, which can pick the other of two stations that were equidistant to within a
    // rounding step. Ghosts cannot appear in either list: Player.SetupPlacementGhost sets
    // ZNetView.m_forceDisableInit, so the ghost has no ZDO and both CraftingStation.Start and
    // StationExtension.Awake bail before registering.
    //
    // UpdateKnownStationsInRange and StationExtension.FindExtensions are deliberately left alone:
    // the first runs at 1 Hz over a few dozen stations, and the second is reachable only through
    // the two-second-throttled GetExtensions.
    //
    // Client: every caller is Hud or Player.UpdatePlacementGhost, all of which need a local
    // player.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(CraftingStation))]
    internal static class StationRangeQueryPatch {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(CraftingStation.HaveBuildStationInRange))]
        private static bool HaveBuildStationInRangePrefix(string name, Vector3 point, ref CraftingStation __result) {
            List<CraftingStation> stations = CraftingStation.m_allStations;
            for (int i = 0; i < stations.Count; i++) {
                CraftingStation station = stations[i];
                if (station.m_name != name) { continue; }

                // Still called for every match, in order: it drives GetExtensions' timed rebuild.
                float range = station.GetStationBuildRange();
                Vector3 stationPos = station.transform.position;

                // Vanilla flattens the query point onto the station's y, so dy is exactly zero.
                float dx = stationPos.x - point.x;
                float dz = stationPos.z - point.z;
                if ((double)(dx * dx + dz * dz) < (double)range * range) {
                    __result = station;
                    return false;
                }
            }

            __result = null;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(CraftingStation.FindClosestStationInRange))]
        private static bool FindClosestStationInRangePrefix(
            string name, Vector3 point, float range, ref CraftingStation __result) {
            CraftingStation closest = null;
            double rangeSquared = (double)range * range;
            double bestSquared = 99999.0 * 99999.0;

            List<CraftingStation> stations = CraftingStation.m_allStations;
            for (int i = 0; i < stations.Count; i++) {
                CraftingStation station = stations[i];
                if (station.m_name != name) { continue; }

                Vector3 stationPos = station.transform.position;
                float dx = stationPos.x - point.x;
                float dy = stationPos.y - point.y;
                float dz = stationPos.z - point.z;
                double squared = dx * dx + dy * dy + dz * dz;

                if (squared < rangeSquared && (squared < bestSquared || closest == null)) {
                    closest = station;
                    bestSquared = squared;
                }
            }

            __result = closest;
            return false;
        }

        [HarmonyPatch(typeof(StationExtension))]
        internal static class ExtensionHooks {
            [HarmonyPrefix]
            [HarmonyPatch(nameof(StationExtension.OtherExtensionInRange))]
            private static bool OtherExtensionInRangePrefix(
                StationExtension __instance, float radius, ref bool __result) {
                // Vanilla re-reads this inside the loop, once per element.
                Vector3 origin = __instance.transform.position;
                double radiusSquared = (double)radius * radius;

                List<StationExtension> extensions = StationExtension.m_allExtensions;
                for (int i = 0; i < extensions.Count; i++) {
                    StationExtension extension = extensions[i];
                    if (extension == __instance) { continue; }

                    Vector3 pos = extension.transform.position;
                    float dx = pos.x - origin.x;
                    float dy = pos.y - origin.y;
                    float dz = pos.z - origin.z;
                    if ((double)(dx * dx + dy * dy + dz * dz) < radiusSquared) {
                        __result = true;
                        return false;
                    }
                }

                __result = false;
                return false;
            }
        }
    }
}
