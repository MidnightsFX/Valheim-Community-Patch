using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Unload Sweep Cost: the object-unload sweep runs on a wall-clock interval instead of on
    // every pass.
    //
    // ZNetScene.RemoveObjects stamps every near and distant ZDO and walks all of m_instances to
    // find the handful that left the loaded rings in the last 33 ms. The sweep costs O(all loaded
    // instances) whether it removes fifty objects or none, and it runs on every
    // CreateDestroyObjects pass while the player moves.
    //
    // A Priority.High prefix skips the whole method until the configured interval (default 100 ms)
    // has passed since the last sweep. A departed object lingers up to that long at the edge of the
    // loaded distance. Time-based rather than every-Nth-pass so that SceneIdleSkipPatch's 1 Hz
    // hygiene pass is always due.
    //
    // Composition: ZoneDiffRemovalPatch replaces this whole stack while its index is healthy, and
    // RemoveObjectsNrePatch honours this prefix's decision through __runOriginal. Both.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class RemoveSweepPacingPatch {
        internal static ConfigEntry<int> SweepIntervalMs;

        internal static void BindConfig() {
            SweepIntervalMs = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Object Unload Sweep Interval",
                100,
                "Milliseconds between object-unload sweeps. Higher recovers more frame time in " +
                "object-heavy areas but lets departed objects linger longer before despawning. " +
                "0 sweeps every pass, exactly like vanilla.",
                advanced: true,
                valMin: 0,
                valMax: 1000);
        }

        private static float _lastSweep;

        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        [HarmonyPatch("RemoveObjects")]
        private static bool RemoveObjectsPrefix() {
            int intervalMs = SweepIntervalMs != null ? SweepIntervalMs.Value : 100;
            if (intervalMs <= 0) { return true; }

            // unscaledTime so a paused or slow-motion game cannot starve the sweep.
            float now = Time.unscaledTime;
            if (now - _lastSweep < intervalMs / 1000f) { return false; }

            _lastSweep = now;
            return true;
        }
    }
}
