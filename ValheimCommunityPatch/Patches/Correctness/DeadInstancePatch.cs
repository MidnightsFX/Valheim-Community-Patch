using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // A ZNetView destroyed outside ZNetScene's removal paths leaves a dead m_instances entry, hanging loading screens.
    // ZNetView.OnDestroy (via TeardownHooks) and an IsAreaReady postfix unregister the dead entry as an unload would.
    // A teleport, dungeon exit or respawn next to such an object finishes loading.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class DeadInstancePatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(DeadInstancePatch),
                ValConfig.SectionCorrectness,
                "Fix Loading Screen Hang",
                true,
                "Unregisters networked objects destroyed without the game being told, most often an Infested " +
                "Mine treasure pile's effect, so loading screens near them no longer wait forever.");
        }

        // Only while IsAreaReady keeps answering no; the scan is one lookup and one native
        // alive-check per created object in the area.
        private const float ScanIntervalSeconds = 1f;

        private static float _nextScan;

        /// <summary>
        /// Called from TeardownHooks for every destroyed non-ghost ZNetView. Every vanilla removal
        /// path has already detached the ZDO by now, so only a view destroyed some other way gets
        /// past the first check. The usual one is vfx_coin_pile_destroyed's child vfx_Place_bed,
        /// which has its own ZNetView and dies with its parent when another player's pick of an
        /// Infested Mine treasure pile destroys it over the network.
        /// </summary>
        internal static void OnViewDestroyed(ZNetView view) {
            ZDO zdo = view.m_zdo;
            if (zdo == null) { return; }
            if (Enabled == null || !Enabled.Value) { return; }

            ZNetScene scene = ZNetScene.instance;
            if (ReferenceEquals(scene, null)) { return; }

            // Not the registered instance: a replacement view already took the ZDO over.
            if (!scene.m_instances.TryGetValue(zdo, out ZNetView registered) || !ReferenceEquals(registered, view)) { return; }

            if (Logger.DebugEnabled) {
                Logger.LogDebug(
                    $"{view.gameObject.name} at {zdo.GetPosition()} was destroyed without the scene " +
                    "being told; unregistered it.");
            }

            Unregister(scene, zdo, view);
        }

        // IsAreaReady counts a dead view as not yet loaded, and Player.UpdateTeleport waits on it
        // with no timeout.
        [HarmonyPostfix]
        [HarmonyPatch(nameof(ZNetScene.IsAreaReady))]
        private static void IsAreaReadyPostfix(ZNetScene __instance, Vector3 point, bool __result) {
            if (__result) { return; }
            if (Enabled == null || !Enabled.Value) { return; }

            float now = Time.unscaledTime;
            if (now < _nextScan) { return; }
            _nextScan = now + ScanIntervalSeconds;

            // Vanilla only fills its area list once the zone has passed the load check; before
            // that the answer is the zone, not an object.
            ZoneSystem zoneSystem = ZoneSystem.instance;
            if (ReferenceEquals(zoneSystem, null) || !zoneSystem.IsZoneLoaded(ZoneSystem.GetZone(point))) { return; }

            List<ZDO> area = __instance.m_tempCurrentObjects;
            for (int i = 0; i < area.Count; i++) {
                ZDO zdo = area[i];

                // An uncreated ZDO is simply still queued; CreateObjects gets to it.
                if (!zdo.Created) { continue; }
                if (!__instance.m_instances.TryGetValue(zdo, out ZNetView view) || view) { continue; }

                GameObject prefab = __instance.GetPrefab(zdo.GetPrefab());
                Logger.LogWarning(
                    $"Loading was waiting on {(prefab != null ? prefab.name : zdo.GetPrefab().ToString())} " +
                    $"at {zdo.GetPosition()}, whose object was destroyed without the game being told, " +
                    "so it would never have finished. Unregistered it.");

                Unregister(__instance, zdo, view);
            }
        }

        // ZNetScene.RemoveObjects' sequence for one instance, minus destroying the GameObject,
        // which is already gone.
        private static void Unregister(ZNetScene scene, ZDO zdo, ZNetView view) {
            scene.m_instances.Remove(zdo);
            if (!ReferenceEquals(view, null) && ReferenceEquals(view.m_zdo, zdo)) { view.m_zdo = null; }

            // Ours and non-persistent: retired. DestroyZDO only queues it until the next
            // ZDOMan.Update, so Created stays set to keep the scene from rebuilding it for that
            // frame; returning it to the pool clears the flag.
            ZDOMan zdoMan = ZDOMan.instance;
            if (!zdo.Persistent && zdo.IsOwner() && !ReferenceEquals(zdoMan, null)) {
                zdoMan.DestroyZDO(zdo);
                return;
            }

            // Otherwise the ZDO outlives its object, as on unload, and CreateObjects rebuilds it.
            zdo.Created = false;
        }
    }
}
