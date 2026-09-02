using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using SoftReferenceableAssets;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Dungeon Load Stall: a dungeon whose room assets fail to load no longer leaves its zone
    // flagged as loading forever.
    //
    // DungeonGenerator.Awake marks its zone as loading, counts its rooms into m_roomsToLoad and
    // loads each asynchronously. OnRoomLoaded returns early for any result other than Succeeded,
    // so a failed room never decrements the counter: Spawn never runs, and ReleaseHeldReferences,
    // the only caller of UnsetLoadingInZone, never runs either. A zone flagged as loading stops
    // spawning objects, and anyone who spawns or teleports into it sits on the loading screen
    // indefinitely. Missing room prefabs (a removed mod) are a different case vanilla already
    // handles; this is a room that resolved but whose asset failed to load.
    //
    // Three prefixes: OnRoomLoaded counts a failed room as finished so the dungeon still spawns;
    // Spawn drops rooms whose asset is not loaded, because PlaceRoom throws on one and a throw
    // there pins the zone by another route; and UnsetLoadingInZone tolerates a ZDO whose sector no
    // longer matches where it was registered, which happens when the ZDO was destroyed and pooled
    // mid-load.
    //
    // Both: the generator runs on a client for the dungeon it stands in and on the host for its
    // own active area.
    [PatchSide(Side.Both)]
    [HarmonyPatch]
    internal static class DungeonZoneLoadPinPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(DungeonZoneLoadPinPatch),
                ValConfig.SectionCorrectness,
                "Fix Dungeon Load Stall",
                true,
                "Keeps a dungeon whose room assets fail to load from leaving its zone flagged as " +
                "loading forever. In vanilla that zone stops spawning objects and anyone who spawns " +
                "or teleports into it never leaves the loading screen.");
        }

        // Takes over only the failure branch; vanilla's success path is untouched.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DungeonGenerator), "OnRoomLoaded")]
        private static bool OnRoomLoadedPrefix(DungeonGenerator __instance, LoadResult result) {
            if (Enabled == null || !Enabled.Value) { return true; }
            if (result == LoadResult.Succeeded) { return true; }

            // A generator that is going away releases the zone from its own OnDestroy.
            if (__instance == null || __instance.gameObject == null) { return true; }

            Logger.LogWarning(
                $"Dungeon at {__instance.transform.position}: a room prefab failed to load ({result}). " +
                "Counting it as finished so the rest of the dungeon still spawns and the zone is not " +
                "left flagged as loading.");

            // Vanilla's tail.
            __instance.m_roomsToLoad--;
            if (__instance.m_roomsToLoad > 0) { return false; }

            __instance.Spawn();
            __instance.ReleaseHeldReferences();
            return false;
        }

        // Runs for every Spawn, because when the failed room is not the last callback to arrive it
        // is vanilla's success path that gets here.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DungeonGenerator), "Spawn")]
        private static void SpawnPrefix(DungeonGenerator __instance) {
            if (Enabled == null || !Enabled.Value) { return; }

            var rooms = __instance.m_loadedRooms;
            if (rooms == null || rooms.Length == 0) { return; }

            // Decide first, compact second: reading Asset reaches into the asset system, and a
            // throw halfway through an in-place compaction would leave duplicated entries. A throw
            // escaping this prefix would be swallowed by the async load callback and pin the zone.
            bool[] loaded = new bool[rooms.Length];
            int kept = 0;
            try {
                for (int i = 0; i < rooms.Length; i++) {
                    DungeonDB.RoomData data = rooms[i].m_roomData;

                    loaded[i] = data != null && data.m_prefab.Asset != null;
                    if (loaded[i]) { kept++; }
                }
            } catch (Exception ex) {
                Logger.LogError(
                    $"Could not check which rooms loaded for the dungeon at {__instance.transform.position}, " +
                    $"so it is being spawned as vanilla would: {ex}");
                return;
            }

            if (kept == rooms.Length) { return; }

            Logger.LogWarning(
                $"Dungeon at {__instance.transform.position}: {rooms.Length - kept} of {rooms.Length} " +
                "room(s) did not load and have been dropped. That part of the dungeon will be missing. " +
                "Placing them anyway throws inside PlaceRoom, which would leave this zone flagged as " +
                "loading and unenterable.");

            int next = 0;
            for (int i = 0; i < rooms.Length; i++) {
                if (loaded[i]) { rooms[next++] = rooms[i]; }
            }

            Array.Resize(ref rooms, kept);
            __instance.m_loadedRooms = rooms;
        }

        // Vanilla indexes m_loadingObjectsInZones straight by zdo.GetSector(). If the ZDO was
        // destroyed mid-load it has been reset and pooled, so its sector is (0,0) or whatever it
        // was re-issued as, the lookup throws KeyNotFoundException or edits the wrong zone, and
        // the real entry is never removed. A scan finds the entry wherever it actually is.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.UnsetLoadingInZone))]
        private static bool UnsetLoadingInZonePrefix(ZoneSystem __instance, ZDO zdo) {
            if (Enabled == null || !Enabled.Value) { return true; }

            // Nothing to unregister; vanilla's answer to either is a NullReferenceException.
            if (zdo == null || __instance.m_loadingObjectsInZones == null) { return false; }

            Dictionary<Vector2i, List<ZDO>> loading = __instance.m_loadingObjectsInZones;

            if (RemoveFrom(loading, zdo.GetSector(), zdo)) { return false; }

            foreach (KeyValuePair<Vector2i, List<ZDO>> pair in loading) {
                if (pair.Value == null || !pair.Value.Contains(zdo)) { continue; }

                // Outside the enumeration, because RemoveFrom can drop the key.
                Vector2i actual = pair.Key;
                RemoveFrom(loading, actual, zdo);
                Logger.LogWarning(
                    $"A dungeon or other loading object reported sector {zdo.GetSector()} but was " +
                    $"registered in {actual}, most likely because its ZDO was destroyed and pooled " +
                    "mid-load. Cleared the real entry; vanilla would have left that zone flagged as " +
                    "loading and unenterable.");
                return false;
            }

            // Nothing registered anywhere: vanilla would throw out of OnDestroy for no gain.
            return false;
        }

        // Drops the zone when its list empties, which is what keeps ContainsKey meaningful in
        // ZoneSystem.IsZoneLoaded.
        private static bool RemoveFrom(Dictionary<Vector2i, List<ZDO>> loading, Vector2i sector, ZDO zdo) {
            if (!loading.TryGetValue(sector, out List<ZDO> inZone)) { return false; }
            if (!inZone.Remove(zdo)) { return false; }

            if (inZone.Count == 0) { loading.Remove(sector); }

            return true;
        }
    }
}
