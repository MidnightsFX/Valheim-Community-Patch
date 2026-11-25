using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using SoftReferenceableAssets;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Location Room Preload: a zone's ground waits for its location and rooms to load, a load that starts too late.
    // Prefixes ZoneSystem.UpdatePrefabLifetimes to preload the generation ring and postfixes PokeCanSpawnLocation.
    // Travelling no longer shows missing ground, and a first-time dungeon no longer loads all its rooms in one frame.
    [PatchSide(Side.Server)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZoneSystem), "PokeCanSpawnLocation")]
    internal static class LocationRoomPreloadPatch {
        internal static ConfigEntry<float> LoadWait;

        internal static void BindConfig() {
            LoadWait = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Location Load Wait",
                1f,
                "Seconds a zone you are about to enter waits, with its ground missing, for its location and dungeon " +
                "rooms to load in the background. After that the rest loads at once, at the cost of a hitch; 0 never waits.",
                advanced: true,
                valMin: 0f,
                valMax: 10f);
        }

        // Answers "not yet" until a first spawn's rooms are in. Proxies still see the location prefab as loaded.
        [HarmonyPostfix]
        private static void Postfix(ZoneSystem __instance, ZoneSystem.ZoneLocation location, bool isFirstSpawn, ref bool __result) {
            if (ApiSwitch.Off || !isFirstSpawn || location == null) { return; }

            ZoneSystem.LocationPrefabLoadData record = Find(__instance, location);
            if (record == null) { return; }

            if (!record.m_isFirstSpawn) { Complete(record, location); }

            // For a first-spawn record this is vanilla's answer: IsLoaded is only set once the
            // rooms are in. A completed proxy record is already IsLoaded, for the prefab alone.
            __result = record.IsLoaded && record.m_roomsToLoad <= 0;

            if (_localDemand > 0) { __result = WaitedEnough(location, __result); }
        }

        private static ZoneSystem.LocationPrefabLoadData Find(ZoneSystem zoneSystem, ZoneSystem.ZoneLocation location) {
            foreach (ZoneSystem.LocationPrefabLoadData record in zoneSystem.m_locationPrefabs) {
                if (record.PrefabAssetID == location.m_prefab.m_assetID) { return record; }
            }

            return null;
        }

        // The load record is kept per prefab, and a nearby copy's proxy loads the prefab alone; that record then reports
        // loaded and Generate loads every room in one frame. This starts the room loads through the record's own fields
        // and vanilla's callback, so vanilla's release frees them.
        private static void Complete(ZoneSystem.LocationPrefabLoadData record, ZoneSystem.ZoneLocation location) {
            record.m_isFirstSpawn = true;

            // Still loading: vanilla's own prefab callback now takes the first-spawn path. Failed:
            // nothing will load, as vanilla.
            if (!record.IsLoaded) { return; }

            // The same lookup vanilla's prefab callback makes for a first spawn.
            DungeonGenerator[] generators = Utils.GetEnabledComponentsInChildren<DungeonGenerator>(record.m_prefab.Asset);
            if (generators.Length == 0) { return; }

            SoftReference<GameObject>[] rooms = generators[0].GetAvailableRoomPrefabs();
            record.m_possibleRooms = rooms;
            record.m_roomsToLoad = rooms.Length;

            LoadedHandler loaded = record.OnRoomLoaded;
            for (int i = 0; i < rooms.Length; i++) { rooms[i].LoadAsync(loaded); }

            Logger.LogDebug(
                $"Location {location.m_prefabName}: its dungeon's {rooms.Length} room prefab(s) were not preloaded, " +
                "because a copy already nearby loaded the location first. Loading them before it generates.");
        }

        // ---- the wait for a zone the player is about to enter -----------------------------------

        // Above zero while PokeLocalZone runs.
        private static int _localDemand;

        private struct Waiting {
            internal float Since;
            internal float LastAsked;
        }

        private static readonly Dictionary<AssetID, Waiting> WaitingSince = new Dictionary<AssetID, Waiting>();

        [HarmonyPatch(typeof(ZoneSystem), "PokeLocalZone")]
        internal static class LocalDemandHook {
            [HarmonyPrefix]
            private static void Prefix() => _localDemand++;

            [HarmonyFinalizer]
            private static void Finalizer() => _localDemand--;
        }

        // Past 'Location Load Wait', generation loads what is missing at once: a hitch rather than a hole in the ground.
        private static bool WaitedEnough(ZoneSystem.ZoneLocation location, bool loaded) {
            AssetID id = location.m_prefab.m_assetID;
            if (loaded) {
                WaitingSince.Remove(id);
                return true;
            }

            float now = Time.realtimeSinceStartup;

            // A wait the player walked away from starts over when they come back.
            if (!WaitingSince.TryGetValue(id, out Waiting waiting) || now - waiting.LastAsked > 2f) {
                waiting.Since = now;
            }

            waiting.LastAsked = now;
            WaitingSince[id] = waiting;

            float limit = LoadWait != null ? LoadWait.Value : 1f;
            if (now - waiting.Since < limit) { return false; }

            WaitingSince.Remove(id);
            Logger.LogDebug(
                $"Location {location.m_prefabName}: still loading after {now - waiting.Since:0.0} s with the player " +
                "about to enter its zone, so it generates now and loads the rest at once.");
            return true;
        }

        // ---- loading ahead ----------------------------------------------------------------------

        private const int TicksBetweenScans = 5;

        private static int _ticks;
        private static Vector2s _lastZone;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(LocationRoomPreloadPatch));

        // Runs once per zone tick, after this tick's generation and before lifetimes count down. Vanilla starts a
        // load only when generation reaches the zone, and skips the ring ahead while nearby zones install.
        [HarmonyPatch(typeof(ZoneSystem), "UpdatePrefabLifetimes")]
        internal static class LoadAheadHook {
            [HarmonyPrefix]
            private static void Prefix(ZoneSystem __instance) {
                if (ApiSwitch.Off) { return; }

                // Only the world owner generates zones.
                ZNet znet = ZNet.instance;
                if (znet == null || !znet.IsServer()) { return; }

                Vector2s zone = ZoneSystem.GetZone(znet.GetReferencePosition());
                if (++_ticks < TicksBetweenScans && zone == _lastZone) { return; }

                _ticks = 0;
                _lastZone = zone;

                LoadAhead(__instance, zone);
                foreach (ZNetPeer peer in znet.GetPeers()) { LoadAhead(__instance, ZoneSystem.GetZone(peer.GetRefPos())); }
            }
        }

        // Vanilla's ghost-ring membership test, as CreateGhostZones uses it.
        private static void LoadAhead(ZoneSystem zoneSystem, Vector2s center) {
            SimulationDistance distance = zoneSystem.m_simulationDistance;
            int radius = distance.TotalSimulationDistance;

            for (int y = center.y - radius; y <= center.y + radius; y++) {
                for (int x = center.x - radius; x <= center.x + radius; x++) {
                    Vector2s zone = new Vector2s(x, y);
                    if (!distance.IsClassic && !zoneSystem.ZonesWithinRadius(center, zone, radius, ghostZone: true)) { continue; }
                    if (!zoneSystem.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance instance) || instance.m_placed) { continue; }
                    if (instance.m_location == null || zoneSystem.IsZoneGenerated(zone)) { continue; }

                    zoneSystem.PokeCanSpawnLocation(instance.m_location, isFirstSpawn: true);
                }
            }
        }
    }
}
