using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Vanilla defect: ZNetScene.RemoveObjects walks every live instance and dereferences its ZDO with
    // no validity check:
    //
    //   foreach (ZNetView znetView in this.m_instances.Values)
    //     if ((int) znetView.GetZDO().TempRemoveEarmark != (int) num)
    //       this.m_tempRemoved.Add(znetView);
    //
    // A ZNetView destroyed by something other than this method (a mod, an unhandled exception mid-spawn,
    // a scene teardown) leaves an entry whose view is Unity-null or whose ZDO is gone. The resulting
    // NullReferenceException aborts the *entire* removal pass for that frame, so nothing is unloaded -
    // and because the orphan is still in m_instances, it throws again on every subsequent frame. The
    // symptom is exception spam plus objects that never despawn, ending in a memory climb.
    //
    // The fix used to run a guarded copy of the loop on every pass, with a Unity-null alive check per
    // entry. That check is a native interop call, and this pass runs over every instance at 30 Hz -
    // profiling attributed on the order of 90 seconds of a day-long session to it, most of it in
    // Object.op_Equality. Orphans, meanwhile, are a once-in-a-session event.
    //
    // So the steady state now runs vanilla's exact loops - no guards, no interop - inside a try/catch,
    // and only a throw drops it into the guarded sweep that cleans the orphan up. The retry from
    // scratch is safe: the earmarks recompute identically within the same frame, the sweep clears
    // m_tempRemoved before filling it, entries the fast pass fully removed are gone from m_instances
    // and invisible to it, and the entry that threw mid-removal is caught by the orphan path because
    // Object.Destroy is deferred to end of frame, so within this call the view is not yet fake-null
    // but its ZDO already is. A non-persistent ZDO whose DestroyZDO was skipped by the throw is still
    // sectored and near, so the next CreateObjects re-instantiates it and the normal path retires it.
    //
    // This is one of the few places where replacing the method wholesale is the right call - the fix is
    // a guard inside a loop that a transpiler cannot express cleanly. The fast pass is deliberately a
    // faithful copy of vanilla, and should be re-checked against the game source on each update.
    //
    // Provenance: guarded-sweep approach as ComfyMods/Scenic (GPL-3.0, redseiko).
    //
    // Composition: SceneIdleSkipPatch (Patches/Performance) sits one level above, as a prefix on
    // CreateDestroyObjects that skips whole unchanged passes - a skipped pass never calls
    // RemoveObjects, a full pass reaches this prefix exactly as vanilla would. That patch relies
    // on this one staying a prefix on RemoveObjects itself, and it defers the orphan detection
    // here by at most its one-second hygiene interval. RemoveSweepPacingPatch
    // (Patches/Performance) sits on RemoveObjects too, at Priority.High, skipping sweeps between
    // its wall-clock interval; the __runOriginal guard below honours that decision without
    // depending on Harmony's skip-remaining-prefixes ordering, and defers orphan detection by at
    // most one more interval.
    //
    // Both: ZNetScene.Update drives CreateDestroyObjects on every peer including a dedicated server,
    // just over a smaller instance set. The failure mode - nothing despawns and memory climbs -
    // applies there too.
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

        // Reused between passes so the guarded path allocates nothing in steady state.
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

        // Vanilla's loops verbatim (ZNetScene.RemoveObjects, ZNetScene.cs:230-253), minus the
        // earmarking already done above, and with GetZDO()/TempRemoveEarmark read as the fields
        // they trivially wrap - at millions of entries per second the call overhead alone was
        // measured in whole percents of frame time, and a null m_zdo dereferences identically.
        // No null guards on purpose: a plain field read and a byte compare per entry is the whole
        // steady-state cost, exactly like vanilla. Anything a broken entry makes this throw -
        // NullReferenceException from a reset ZDO, MissingReferenceException from a destroyed
        // view's gameObject, or whatever a modded component's teardown raises - is the caller's
        // signal to fall back to the guarded sweep.
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

        // The recovery path: the per-entry alive checks the fast pass skips. Restartable from scratch
        // after a partial fast pass - see the header for why.
        private static void GuardedSweep(ZNetScene scene, byte earmark) {
            scene.m_tempRemoved.Clear();
            OrphanedKeys.Clear();

            foreach (KeyValuePair<ZDO, ZNetView> instance in scene.m_instances) {
                ZNetView view = instance.Value;

                // Unity-null view, or a view whose ZDO was already released: nothing left to unload
                // through the normal path, but the dictionary entry has to go or we revisit it every
                // frame.
                //
                // A view that is still alive but has lost its ZDO has to be destroyed here as well,
                // not merely dropped. m_instances is the only handle anything has on it, so once the
                // key is gone nothing can ever reach the GameObject again and it is stranded in the
                // scene for the rest of the session - a slow leak in exactly the situation this fix
                // exists to survive.
                if (view == null || view.GetZDO() == null) {
                    OrphanedKeys.Add(instance.Key);

                    if (view != null) {
                        // A modded component's OnDestroy can throw; the key still has to go.
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
