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
    // This is one of the few places where replacing the method wholesale is the right call - the fix is
    // a guard inside a loop that a transpiler cannot express cleanly. It is deliberately a faithful
    // copy of vanilla plus guards, and should be re-checked against the game source on each update.
    //
    // Provenance: same approach as ComfyMods/Scenic (GPL-3.0, redseiko).
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
                "Skips orphaned entries during object unloading instead of throwing. In vanilla a single " +
                "orphan aborts the whole unload pass every frame, so nothing despawns and the log fills " +
                "with NullReferenceExceptions.");
        }

        // Reused between passes so the guarded path allocates nothing in steady state.
        private static readonly List<ZDO> OrphanedKeys = new List<ZDO>();

        [HarmonyPrefix]
        [HarmonyPatch("RemoveObjects")]
        private static bool RemoveObjectsPrefix(
            ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects) {
            if (Enabled == null || !Enabled.Value) { return true; }

            byte earmark = (byte)(Time.frameCount & byte.MaxValue);

            for (int i = 0; i < currentNearObjects.Count; i++) { currentNearObjects[i].TempRemoveEarmark = earmark; }
            for (int i = 0; i < currentDistantObjects.Count; i++) { currentDistantObjects[i].TempRemoveEarmark = earmark; }

            __instance.m_tempRemoved.Clear();
            OrphanedKeys.Clear();

            foreach (KeyValuePair<ZDO, ZNetView> instance in __instance.m_instances) {
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
                    if (view != null) { Object.Destroy(view.gameObject); }
                    continue;
                }

                if (view.GetZDO().TempRemoveEarmark != earmark) { __instance.m_tempRemoved.Add(view); }
            }

            for (int i = 0; i < __instance.m_tempRemoved.Count; i++) {
                ZNetView view = __instance.m_tempRemoved[i];
                ZDO zdo = view.GetZDO();

                view.ResetZDO();
                Object.Destroy(view.gameObject);

                if (!zdo.Persistent && zdo.IsOwner()) { ZDOMan.instance.DestroyZDO(zdo); }

                __instance.m_instances.Remove(zdo);
            }

            if (OrphanedKeys.Count > 0) {
                for (int i = 0; i < OrphanedKeys.Count; i++) { __instance.m_instances.Remove(OrphanedKeys[i]); }

                Logger.LogDebug($"Dropped {OrphanedKeys.Count} orphaned scene instance(s).");
                OrphanedKeys.Clear();
            }

            return false;
        }
    }
}
