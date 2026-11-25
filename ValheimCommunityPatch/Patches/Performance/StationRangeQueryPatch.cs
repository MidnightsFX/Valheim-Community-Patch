using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Station Range Scans: build mode runs three station range scans every frame, a square root per candidate.
    // Prefixes HaveBuildStationInRange, FindClosestStationInRange and OtherExtensionInRange with squared distances.
    // Building near many crafting stations costs less per frame, with the same answers to within a rounding step.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(CraftingStation))]
    internal static class StationRangeQueryPatch {
        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(StationRangeQueryPatch));

        [HarmonyPrefix]
        [HarmonyPatch(nameof(CraftingStation.HaveBuildStationInRange))]
        private static bool HaveBuildStationInRangePrefix(string name, Vector3 point, ref CraftingStation __result) {
            if (ApiSwitch.Off) { return true; }

            List<CraftingStation> stations = CraftingStation.m_allStations;
            for (int i = 0; i < stations.Count; i++) {
                CraftingStation station = stations[i];
                if (station.m_name != name) { continue; }

                // Still called for every match, in order: it drives GetExtensions' timed rebuild.
                float range = station.GetStationBuildRange();
                Vector3 stationPos = station.transform.position;

                // Vanilla flattens the query point onto the station's y, so dy is exactly zero. Comparing
                // squares counts a station whose distance rounds to exactly the range as in range, where
                // vanilla did not: a band about 3e-7 of the range wide.
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

        // Ties break on the exact squared distance, so of two stations equidistant to within a rounding
        // step the other one may win.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(CraftingStation.FindClosestStationInRange))]
        private static bool FindClosestStationInRangePrefix(
            string name, Vector3 point, float range, ref CraftingStation __result) {
            if (ApiSwitch.Off) { return true; }

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
                if (ApiSwitch.Off) { return true; }

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
