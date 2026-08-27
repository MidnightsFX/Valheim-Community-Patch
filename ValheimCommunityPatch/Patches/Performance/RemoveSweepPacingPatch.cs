using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: every CreateDestroyObjects pass (30 Hz whenever the scene is not provably
    // idle - which is continuously while the player moves) runs a full removal sweep,
    // ZNetScene.RemoveObjects: stamp an earmark on every near and distant ZDO, then walk all of
    // m_instances comparing earmarks, to find the handful of objects that left the active ring in
    // the last 33 ms. The sweep is O(all loaded instances) whether it removes fifty objects or
    // none. Measured in a 60-90k-instance base: ~77 ms per moving second - ~8% of frame time -
    // of which 45 ms/s was 2.7 million trivial ZNetView.GetZDO() calls.
    //
    // Fix: run the sweep only when a configurable wall-clock interval (default 100 ms) has passed
    // since the last one, and skip the whole method - stamps, walk, everything - in between. An
    // object that leaves the ring lingers up to that interval longer before despawning, at the
    // edge of the loaded distance where nothing can be watching it. Nothing else moves: the
    // earmark is stamped and consumed inside the same due pass (its only validity window),
    // CreateObjects never depends on removal having run, and despawn-by-ZDO-death goes through
    // ZNetScene.OnZDODestroyed, which is untouched.
    //
    // Time-based rather than every-Nth-pass on purpose: at 30 Hz it yields 10 Hz sweeps, but when
    // SceneIdleSkipPatch throttles traffic to its 1 Hz hygiene pass, every hygiene pass is
    // automatically due - the idle path's safety latency stays exactly what it was.
    //
    // This throttles CHECKING for work, not doing it - the opposite of the removed spawn-burst
    // budget, which throttled work that had to happen and thereby repeated its per-pass fixed
    // costs for minutes. A skipped check makes the next sweep no more expensive, so total work
    // falls by a strict factor of the interval and nothing accumulates.
    //
    // Composition, top to bottom: SceneIdleSkipPatch skips whole provably-unchanged passes above
    // CreateDestroyObjects; this prefix paces RemoveObjects itself; RemoveObjectsNrePatch's
    // prefix (Patches/Correctness) replaces the sweep body on the passes that do run. This prefix
    // is Priority.High so it decides first, and that prefix additionally guards on __runOriginal
    // so the interlock does not depend on Harmony's skip-remaining-prefixes ordering. With the
    // NRE fix toggled off this gate paces vanilla's method just the same.
    //
    // Both: a dedicated server runs the same sweep over its own active set at the same rate.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class RemoveSweepPacingPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> SweepIntervalMs;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(RemoveSweepPacingPatch),
                ValConfig.SectionPerformance,
                "Fix Unload Sweep Cost",
                true,
                "Runs the object-unload sweep on a wall-clock interval instead of thirty times a " +
                "second. The sweep walks every loaded object to find the few that left the " +
                "loaded area, so at large-base object counts it is a steady share of frame time " +
                "whenever the scene is changing. Objects leaving the area despawn up to a tenth " +
                "of a second later, at the far edge of the loaded distance.");

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
            if (Enabled == null || !Enabled.Value) { return true; }

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
