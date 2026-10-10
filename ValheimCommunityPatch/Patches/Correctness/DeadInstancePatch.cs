using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Loading Screen Hang: a networked object destroyed without the scene being told is
    // unregistered, so a teleport, dungeon exit or respawn next to it can finish loading.
    //
    // ZNetScene clears an m_instances entry only on its own removal paths, each of which detaches
    // the ZDO first. A ZNetView destroyed any other way leaves its entry pointing at a dead view
    // while the ZDO stays loaded and marked created, so the object is never rebuilt either.
    // IsAreaReady counts the dead view as not yet loaded, and Player.UpdateTeleport waits on it
    // with no timeout. The usual source is vanilla's vfx_coin_pile_destroyed, the Infested Mine
    // treasure pile's pick effect: its child vfx_Place_bed has a ZNetView of its own, and on every
    // other player's client the parent is destroyed over the network before the child's own timer
    // runs out, taking the child down with its hierarchy.
    //
    // TeardownHooks' ZNetView.OnDestroy postfix hands over any view that still holds its ZDO and
    // is still the registered instance, and it gets vanilla's unload sequence for one object: drop
    // the entry, then destroy the ZDO if it is non-persistent and this peer's, or clear Created so
    // the scene rebuilds it. A postfix on IsAreaReady repairs any dead entry left in the area it
    // was asked about, at most once a second while it answers no, and names the object in the log.
    //
    // Both: any peer's scene can be left holding a dead view; the loading screens that wait on one
    // are a client's.
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
                "Unregisters a networked object that was destroyed without the game's object " +
                "manager being told, most often the coin effect left when another player opens an " +
                "Infested Mine treasure pile. In vanilla the leftover entry keeps the loading screen " +
                "up forever for a dungeon exit, portal trip or respawn within one zone of it.");
        }

        // Only while IsAreaReady keeps answering no; the scan is one lookup and one native
        // alive-check per created object in the area.
        private const float ScanIntervalSeconds = 1f;

        private static float _nextScan;

        /// <summary>
        /// Called from TeardownHooks for every destroyed non-ghost ZNetView. Every vanilla removal
        /// path has already detached the ZDO by now, so only a view destroyed some other way gets
        /// past the first check.
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
