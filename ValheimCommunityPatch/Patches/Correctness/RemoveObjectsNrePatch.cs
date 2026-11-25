using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // ZNetScene.RemoveObjects has no validity checks, so one orphaned instance throws every frame and nothing despawns.
    // Prefixes RemoveObjects with vanilla's loops in a try/catch, falling back to a guarded sweep that drops orphans.
    // Objects keep unloading after an orphan appears, so the log stays clean and memory stops climbing.
    // Provenance: ComfyMods/Scenic (GPL-3.0, redseiko).
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class RemoveObjectsNrePatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(RemoveObjectsNrePatch),
                ValConfig.SectionCorrectness,
                "Fix Object Unload Crash",
                true,
                "Keeps objects unloading when an orphaned scene entry is left behind, where vanilla throws " +
                "every frame and nothing despawns again.");
        }

        private static readonly List<ZDO> OrphanedKeys = new List<ZDO>();

        // Priority.Last, so other mods' prefixes have already added what they keep loaded to the lists this stamps.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("RemoveObjects")]
        private static bool RemoveObjectsPrefix(
            ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects,
            bool __runOriginal) {
            // An earlier prefix (RemoveSweepPacingPatch or another mod's) already skipped this sweep.
            if (!__runOriginal) { return false; }

            // It shares this priority, so it may run after this prefix; it finishes every pass itself.
            if (Performance.ZoneDiffRemovalPatch.Engaged) { return true; }

            if (Enabled == null || !Enabled.Value) { return true; }

            byte earmark = (byte)(Time.frameCount & byte.MaxValue);

            for (int i = 0; i < currentNearObjects.Count; i++) { currentNearObjects[i].m_tempRemoveEarmark = earmark; }
            for (int i = 0; i < currentDistantObjects.Count; i++) { currentDistantObjects[i].m_tempRemoveEarmark = earmark; }

            // Restarting from scratch is safe: the earmarks recompute identically within the frame, and Object.Destroy
            // is deferred, so the entry that threw is still visible to the guarded pass.
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
        // No per-entry guards: a native alive-check per instance at 30 Hz cost whole percents of frame time.
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
