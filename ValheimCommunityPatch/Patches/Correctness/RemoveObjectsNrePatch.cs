using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Object Unload Crash: one orphaned scene instance no longer aborts every unload pass.
    //
    // ZNetScene.RemoveObjects walks every live instance and dereferences its ZDO with no validity
    // check. A ZNetView destroyed by something other than this method (a mod, an exception
    // mid-spawn, a scene teardown) leaves an entry whose view or ZDO is gone, the resulting
    // NullReferenceException aborts the whole pass, and because the orphan stays in m_instances it
    // throws again every frame: nothing despawns and memory climbs.
    //
    // A prefix replaces the method with a copy of vanilla's loops inside a try/catch. The fast
    // path has no per-entry guards (a native alive-check per instance at 30 Hz was measured in
    // whole percents of frame time), and only a throw drops into GuardedSweep, which checks every
    // entry, destroys and drops the orphans, and finishes the pass. Restarting from scratch is
    // safe: the earmarks recompute identically within the frame, and Object.Destroy is deferred,
    // so the entry that threw is still visible to the guarded pass.
    //
    // Composition: SceneIdleSkipPatch and RemoveSweepPacingPatch decide above this whether a pass
    // runs at all; the __runOriginal check honours the pacing prefix. ZoneDiffRemovalPatch replaces
    // this prefix entirely while its index is healthy and borrows GuardedSweep as its own
    // fallback. Both: every peer runs this pass. Provenance: ComfyMods/Scenic (GPL-3.0, redseiko).
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class RemoveObjectsNrePatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(RemoveObjectsNrePatch),
                ValConfig.SectionCorrectness,
                "Fix Object Unload Crash",
                true,
                "Recovers from orphaned entries during object unloading instead of throwing. In vanilla " +
                "a single orphan aborts the whole unload pass every frame, so nothing despawns and the " +
                "log fills with NullReferenceExceptions.");
        }

        private static readonly List<ZDO> OrphanedKeys = new List<ZDO>();

        [HarmonyPrefix]
        [HarmonyPatch("RemoveObjects")]
        private static bool RemoveObjectsPrefix(
            ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects,
            bool __runOriginal) {
            // A higher-priority prefix (RemoveSweepPacingPatch) already skipped this sweep.
            if (!__runOriginal) { return false; }

            if (Enabled == null || !Enabled.Value) { return true; }

            byte earmark = (byte)(Time.frameCount & byte.MaxValue);

            for (int i = 0; i < currentNearObjects.Count; i++) { currentNearObjects[i].m_tempRemoveEarmark = earmark; }
            for (int i = 0; i < currentDistantObjects.Count; i++) { currentDistantObjects[i].m_tempRemoveEarmark = earmark; }

            try {
                FastPass(__instance, earmark);
            } catch (Exception e) {
                Logger.LogDebug($"Object unload hit an orphaned instance ({e.GetType().Name}); running guarded sweep.");
                GuardedSweep(__instance, earmark);
            }

            return false;
        }

        // Vanilla's loops (ZNetScene.RemoveObjects), with GetZDO()/TempRemoveEarmark read as the
        // fields they wrap. Anything a broken entry makes this throw is the signal to fall back.
        private static void FastPass(ZNetScene scene, byte earmark) {
            scene.m_tempRemoved.Clear();

            foreach (ZNetView view in scene.m_instances.Values) {
                if (view.m_zdo.m_tempRemoveEarmark != earmark) { scene.m_tempRemoved.Add(view); }
            }

            for (int i = 0; i < scene.m_tempRemoved.Count; i++) {
                ZNetView view = scene.m_tempRemoved[i];
                ZDO zdo = view.m_zdo;

                view.ResetZDO();
                UnityEngine.Object.Destroy(view.gameObject);

                if (!zdo.Persistent && zdo.IsOwner()) { ZDOMan.instance.DestroyZDO(zdo); }

                scene.m_instances.Remove(zdo);
            }
        }

        /// <summary>
        /// The recovery path: vanilla's pass with a validity check per entry. Orphans are destroyed
        /// and dropped from m_instances. Restartable after a partial fast pass; also used by
        /// ZoneDiffRemovalPatch under the same earmark contract.
        /// </summary>
        internal static void GuardedSweep(ZNetScene scene, byte earmark) {
            scene.m_tempRemoved.Clear();
            OrphanedKeys.Clear();

            foreach (KeyValuePair<ZDO, ZNetView> instance in scene.m_instances) {
                ZNetView view = instance.Value;

                // A view that is alive but lost its ZDO must be destroyed as well as dropped:
                // m_instances is the only handle on it, so dropping the key alone strands the
                // GameObject in the scene for the rest of the session.
                if (view == null || view.GetZDO() == null) {
                    OrphanedKeys.Add(instance.Key);

                    if (view != null) {
                        try { UnityEngine.Object.Destroy(view.gameObject); }
                        catch (Exception e) { Logger.LogDebug($"Destroying an orphaned view failed: {e.GetType().Name}."); }
                    }

                    continue;
                }

                if (view.GetZDO().TempRemoveEarmark != earmark) { scene.m_tempRemoved.Add(view); }
            }

            for (int i = 0; i < scene.m_tempRemoved.Count; i++) {
                ZNetView view = scene.m_tempRemoved[i];
                ZDO zdo = view.GetZDO();

                view.ResetZDO();
                UnityEngine.Object.Destroy(view.gameObject);

                if (!zdo.Persistent && zdo.IsOwner()) { ZDOMan.instance.DestroyZDO(zdo); }

                scene.m_instances.Remove(zdo);
            }

            if (OrphanedKeys.Count > 0) {
                for (int i = 0; i < OrphanedKeys.Count; i++) { scene.m_instances.Remove(OrphanedKeys[i]); }

                Logger.LogDebug($"Dropped {OrphanedKeys.Count} orphaned scene instance(s).");
                OrphanedKeys.Clear();
            }
        }
    }
}
