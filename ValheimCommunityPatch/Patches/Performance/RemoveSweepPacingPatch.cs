using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Unload Sweep Cost: ZNetScene.RemoveObjects walks every loaded object on each pass, whether or not any left.
    // Prefixes RemoveObjects to skip the sweep until 'Object Unload Sweep Interval' has passed since the last one.
    // Moving through object-heavy areas costs less frame time; a departed object lingers up to the interval longer.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class RemoveSweepPacingPatch {
        internal static ConfigEntry<int> SweepIntervalMs;

        internal static void BindConfig() {
            SweepIntervalMs = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Object Unload Sweep Interval",
                100,
                "Milliseconds between object-unload sweeps; 0 sweeps every pass, as vanilla. Higher saves " +
                "more frame time in object-heavy areas but lets departed objects linger longer.",
                advanced: true,
                valMin: 0,
                valMax: 1000);
        }

        private static float _lastSweep;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(RemoveSweepPacingPatch));

        // Wall-clock rather than every Nth pass, so SceneIdleSkipPatch's 1 Hz hygiene pass is always due.
        // Stands down while ZoneDiffRemovalPatch is engaged: its prefix runs below this one and must see
        // every pass. RemoveObjectsNrePatch honours this decision through __runOriginal.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        [HarmonyPatch("RemoveObjects")]
        private static bool RemoveObjectsPrefix() {
            if (ApiSwitch.Off || ZoneDiffRemovalPatch.Engaged) { return true; }

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
