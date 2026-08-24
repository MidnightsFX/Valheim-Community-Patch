using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using SoftReferenceableAssets;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Vanilla defect: a dungeon whose room prefabs do not all load leaves its zone flagged as
    // "loading" for good, and a zone in that state is one nobody can spawn or teleport into.
    //
    // DungeonGenerator.Awake loads its saved rooms asynchronously (DungeonGenerator.cs:162). The
    // first thing it does is claim the zone:
    //
    //   this.m_zdoSetToBeLoadingInZone = this.m_nview.GetZDO();
    //   ZoneSystem.instance.SetLoadingInZone(this.m_zdoSetToBeLoadingInZone);
    //   this.m_roomsToLoad = this.m_loadedRooms.Length;
    //   ... LoadAsync(new LoadedHandler(this.OnRoomLoaded)) per room ...
    //
    // and the claim is only ever given back through OnRoomLoaded (:179):
    //
    //   private void OnRoomLoaded(AssetID assetID, LoadResult result) {
    //     if (result != LoadResult.Succeeded || this == null || this.gameObject == null) return;
    //     --this.m_roomsToLoad;
    //     if (this.m_roomsToLoad > 0) return;
    //     this.Spawn();
    //     this.ReleaseHeldReferences();
    //   }
    //
    // A room that comes back Failed or Aborted takes the early return, so it never decrements the
    // counter it was counted into. m_roomsToLoad can then never reach zero: Spawn never runs, the
    // dungeon interior never appears, and ReleaseHeldReferences - the only thing that calls
    // UnsetLoadingInZone - never runs either. The zone stays in ZoneSystem.m_loadingObjectsInZones
    // until the generator itself unloads, which will not happen while a player is standing in it.
    //
    // A pinned zone is not a cosmetic problem:
    //
    //   * ZoneSystem.IsZoneReadyForType (ZoneSystem.cs:1834) returns false, so
    //     ZNetScene.CreateObjectsSorted stops creating that zone's objects.
    //   * ZNetScene.IsAreaReady (ZNetScene.cs:122) returns false, so Game.UpdateRespawn
    //     (Game.cs:316/343/366) and Player's teleport completion (Player.cs:4166) never finish.
    //     Anyone spawning into or teleporting to that zone sits on the loading screen indefinitely.
    //
    // Note that missing *rooms* are already handled: Load compacts and then trims m_loadedRooms to
    // the rooms DungeonDB could resolve (DungeonGenerator.cs:406-410), so uninstalling a room-adding
    // mod is not this bug. This is specifically a room that resolved but whose asset failed to load.
    //
    // Fix, in three parts:
    //
    //   * Take over OnRoomLoaded's failure branch so it accounts for the room the same way a
    //     successful load would. Vanilla's success path is left entirely alone.
    //   * Drop rooms that did not load before Spawn walks them. PlaceRoom's first statement is
    //     roomData.m_prefab.Asset.GetComponent<Room>() (:670), which throws on an unloaded asset -
    //     and a throw there puts us straight back to a pinned zone by a different route. This is
    //     also the part that matters when the failure is not the last callback to arrive, because
    //     then it is vanilla's own success path that calls Spawn.
    //   * Stop UnsetLoadingInZone throwing when the generator's cached ZDO no longer reports the
    //     sector it was filed under. See the comment on that prefix.
    //
    // A dungeon missing a room or two is a poor outcome. It is a much better one than a zone the
    // server can never finish loading.
    //
    // Both: DungeonGenerator.Awake runs wherever the generator is instantiated - on a client for the
    // dungeon it is standing in, and on the host for its own active area.
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

        // Takes over only the branch vanilla drops on the floor. A failed room still has to be
        // accounted for, or the counter it was added to can never reach zero.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DungeonGenerator), "OnRoomLoaded")]
        private static bool OnRoomLoadedPrefix(DungeonGenerator __instance, LoadResult result) {
            if (Enabled == null || !Enabled.Value) { return true; }

            // Vanilla's own path, untouched.
            if (result == LoadResult.Succeeded) { return true; }

            // The generator is going away regardless, and its OnDestroy releases the zone. Let
            // vanilla take its identical early return rather than duplicate the decision here.
            if (__instance == null || __instance.gameObject == null) { return true; }

            Logger.LogWarning(
                $"Dungeon at {__instance.transform.position}: a room prefab failed to load ({result}). " +
                "Counting it as finished so the rest of the dungeon still spawns and the zone is not " +
                "left flagged as loading.");

            // The rest mirrors vanilla's tail exactly (DungeonGenerator.cs:183-187).
            __instance.m_roomsToLoad--;
            if (__instance.m_roomsToLoad > 0) { return false; }

            __instance.Spawn();
            __instance.ReleaseHeldReferences();
            return false;
        }

        // Runs for every Spawn, not just the ones reached through the prefix above: when the failed
        // room is not the last callback to arrive, it is vanilla's success path that gets here.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DungeonGenerator), "Spawn")]
        private static void SpawnPrefix(DungeonGenerator __instance) {
            if (Enabled == null || !Enabled.Value) { return; }

            var rooms = __instance.m_loadedRooms;
            if (rooms == null || rooms.Length == 0) { return; }

            // Decided before anything moves, and recorded, so the compaction below cannot throw.
            // Reading Asset reaches into the asset system; compacting in place as we went would
            // leave the array half-shifted if it threw partway, and vanilla's Spawn would then walk
            // duplicated entries - a worse outcome than the one being fixed. The try is here for the
            // same reason: a throw escaping a prefix propagates out of an async load callback, gets
            // swallowed by Unity, and pins the zone via the fix meant to prevent exactly that.
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

            // Compaction proper, over the recorded verdicts only - no asset reads, nothing to throw.
            int next = 0;
            for (int i = 0; i < rooms.Length; i++) {
                if (loaded[i]) { rooms[next++] = rooms[i]; }
            }

            Array.Resize(ref rooms, kept);
            __instance.m_loadedRooms = rooms;
        }

        // Vanilla indexes straight into the dictionary:
        //
        //   public void UnsetLoadingInZone(ZDO zdo) {
        //     Vector2i sector = zdo.GetSector();
        //     this.m_loadingObjectsInZones[sector].Remove(zdo);
        //     ...
        //
        // DungeonGenerator holds the ZDO it registered in m_zdoSetToBeLoadingInZone and hands that
        // same object back on OnDestroy (DungeonGenerator.cs:83-86). If the ZDO was destroyed in the
        // meantime the object is no longer the one that was filed: ZDOMan.HandleDestroyedZDO calls
        // ZDOPool.Release synchronously (ZDOMan.cs:548) and ZDO.Reset zeroes m_sector (ZDO.cs:60),
        // while Object.Destroy defers OnDestroy to the end of the frame. So the lookup runs against
        // sector (0,0) - or against whichever sector the pooled ZDO has since been re-issued into -
        // and throws a KeyNotFoundException, or quietly edits an unrelated zone's list. Either way
        // the entry that was really made is never removed, and the zone is pinned exactly as above.
        //
        // Falling back to a scan finds the entry wherever it actually is. The dictionary holds one
        // key per zone currently loading something, which is a handful at most.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.UnsetLoadingInZone))]
        private static bool UnsetLoadingInZonePrefix(ZoneSystem __instance, ZDO zdo) {
            if (Enabled == null || !Enabled.Value) { return true; }

            // Not "return true": with no ZDO and no dictionary there is nothing to unregister, and
            // vanilla's answer to both is a NullReferenceException out of OnDestroy.
            if (zdo == null || __instance.m_loadingObjectsInZones == null) { return false; }

            Dictionary<Vector2i, List<ZDO>> loading = __instance.m_loadingObjectsInZones;

            // The common case: the ZDO is exactly where its own sector says it is.
            if (RemoveFrom(loading, zdo.GetSector(), zdo)) { return false; }

            foreach (KeyValuePair<Vector2i, List<ZDO>> pair in loading) {
                if (pair.Value == null || !pair.Value.Contains(zdo)) { continue; }

                // Deferred out of the enumeration: RemoveFrom can drop the key.
                Vector2i actual = pair.Key;
                RemoveFrom(loading, actual, zdo);
                Logger.LogWarning(
                    $"A dungeon or other loading object reported sector {zdo.GetSector()} but was " +
                    $"registered in {actual}, most likely because its ZDO was destroyed and pooled " +
                    "mid-load. Cleared the real entry; vanilla would have left that zone flagged as " +
                    "loading and unenterable.");
                return false;
            }

            // Nothing registered anywhere. Vanilla would throw a KeyNotFoundException out of
            // DungeonGenerator.OnDestroy and abandon the rest of it; there is nothing to clean up,
            // so returning quietly is strictly better.
            return false;
        }

        /// Removes <paramref name="zdo"/> from one zone's list, dropping the zone when it empties -
        /// which is what keeps m_loadingObjectsInZones.ContainsKey meaningful in IsZoneLoaded.
        private static bool RemoveFrom(Dictionary<Vector2i, List<ZDO>> loading, Vector2i sector, ZDO zdo) {
            if (!loading.TryGetValue(sector, out List<ZDO> inZone)) { return false; }
            if (!inZone.Remove(zdo)) { return false; }

            if (inZone.Count == 0) { loading.Remove(sector); }

            return true;
        }
    }
}
