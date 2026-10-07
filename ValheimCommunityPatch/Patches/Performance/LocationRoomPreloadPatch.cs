using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Location Room Preload: a location generated for the first time waits for its dungeon's
    // room prefabs to load in the background, as the game intends, even when a copy of that
    // location already standing nearby asked for the location prefab first.
    //
    // Before generating a zone that holds an unplaced location, ZoneSystem.SpawnZone asks
    // PokeCanSpawnLocation whether the location is loaded, and for a first spawn that load covers
    // every room prefab of the location's dungeon too. The load record is kept per prefab, though,
    // and an existing location's proxy asks as well, for the location prefab alone. When the
    // proxy's record is the one found, it already reports loaded, the zone generates at once, and
    // DungeonGenerator.Generate loads every room prefab synchronously in that frame: well over a
    // second for the Deep North halls.
    //
    // A postfix on PokeCanSpawnLocation, for a first spawn only, completes a record a proxy made:
    // it starts the room prefab loads that record skipped, through the record's own fields and
    // vanilla's own callback so vanilla's release frees them, and answers "not yet" until they are
    // in. A record made for a first spawn is answered exactly as vanilla answers it, and proxies
    // still see the location prefab as loaded.
    //
    // Server: only the world owner generates zones.
    [PatchSide(Side.Server)]
    [HarmonyPatch(typeof(ZoneSystem), "PokeCanSpawnLocation")]
    internal static class LocationRoomPreloadPatch {
        [HarmonyPostfix]
        private static void Postfix(ZoneSystem __instance, ZoneSystem.ZoneLocation location, bool isFirstSpawn, ref bool __result) {
            if (!isFirstSpawn || location == null) { return; }

            ZoneSystem.LocationPrefabLoadData record = Find(__instance, location);
            if (record == null) { return; }

            if (!record.m_isFirstSpawn) { Complete(record, location); }

            // For a first-spawn record this is vanilla's answer: IsLoaded is only set once the
            // rooms are in. A completed proxy record is already IsLoaded, for the prefab alone.
            __result = record.IsLoaded && record.m_roomsToLoad <= 0;
        }

        private static ZoneSystem.LocationPrefabLoadData Find(ZoneSystem zoneSystem, ZoneSystem.ZoneLocation location) {
            foreach (ZoneSystem.LocationPrefabLoadData record in zoneSystem.m_locationPrefabs) {
                if (record.PrefabAssetID == location.m_prefab.m_assetID) { return record; }
            }

            return null;
        }

        private static void Complete(ZoneSystem.LocationPrefabLoadData record, ZoneSystem.ZoneLocation location) {
            record.m_isFirstSpawn = true;

            // Still loading: vanilla's own prefab callback now takes the first-spawn path. Failed:
            // nothing will load, as vanilla.
            if (!record.IsLoaded) { return; }

            // The same lookup vanilla's prefab callback makes for a first spawn.
            DungeonGenerator[] generators = Utils.GetEnabledComponentsInChildren<DungeonGenerator>(record.m_prefab.Asset);
            if (generators.Length == 0) { return; }

            SoftReferenceableAssets.SoftReference<GameObject>[] rooms = generators[0].GetAvailableRoomPrefabs();
            record.m_possibleRooms = rooms;
            record.m_roomsToLoad = rooms.Length;

            SoftReferenceableAssets.LoadedHandler loaded = record.OnRoomLoaded;
            for (int i = 0; i < rooms.Length; i++) { rooms[i].LoadAsync(loaded); }

            Logger.LogDebug(
                $"Location {location.m_prefabName}: its dungeon's {rooms.Length} room prefab(s) were not preloaded, " +
                "because a copy already nearby loaded the location first. Loading them before it generates.");
        }
    }
}
