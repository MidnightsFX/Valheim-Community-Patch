using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Destination Terrain Delay: the terrain around a teleport's destination, and around your
    // bed while you are dead, starts building as soon as the destination is known instead of on
    // arrival.
    //
    // A zone is installed only once the terrain build thread has generated its heightmap, and the
    // game first asks for that when the player's reference position reaches the zone: after a
    // teleport's 2 s fade, or after the 10 s death delay. The build thread sits idle through both,
    // then the whole ring is queued at once while the loading screen is up.
    //
    // Postfixes on Player.TeleportTo (the local player's accepted teleport) and Game.RequestRespawn
    // (after a death, when the profile has a bed spawn point) call HeightmapBuilder.IsTerrainReady
    // for each zone of the destination's near ring that is not installed yet, nearest first, with
    // the arguments ZoneSystem.SpawnZone passes. IsTerrainReady only queues a build, and SpawnZone
    // later finds the result waiting, so no step of the game's own loading is skipped. Finished
    // results are evicted oldest first past the ready cap, so the request stops short of it, less
    // what is already queued or finished and the nine distant-terrain tiles a teleport re-requests
    // on arrival.
    //
    // Client: it acts on the local player's teleport or death, and a dedicated server installs no
    // zones around players.
    [PatchSide(Side.Client)]
    [HarmonyPatch]
    internal static class DestinationTerrainDelayPatch {
        // TerrainLod's ring: queued together once the camera has moved, and held in the ready list
        // until all nine are built.
        private const int DistantTileReserve = 9;

        private static readonly List<Vector2s> Ring = new List<Vector2s>();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
        private static void TeleportToPostfix(Player __instance, Vector3 pos, bool __result) {
            if (!__result || __instance != Player.m_localPlayer) { return; }

            Prewarm(pos, "teleport destination");
        }

        // Player.OnDeath starts the respawn delay here. After a death FindSpawnPoint skips the
        // logout point and goes to the bed, when there is one.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Game), nameof(Game.RequestRespawn))]
        private static void RequestRespawnPostfix(Game __instance, bool afterDeath) {
            if (!afterDeath) { return; }

            PlayerProfile profile = __instance.GetPlayerProfile();
            if (profile == null || !profile.HaveCustomSpawnPoint()) { return; }

            Prewarm(profile.GetCustomSpawnPoint(), "bed");
        }

        private static void Prewarm(Vector3 point, string what) {
            ZoneSystem zoneSystem = ZoneSystem.instance;
            WorldGenerator worldGen = WorldGenerator.instance;

            // The private field, not the property, which would start a builder or warn after
            // shutdown. The terrain the player stands on means the thread is already running.
            HeightmapBuilder builder = HeightmapBuilder.m_instance;
            if (zoneSystem == null || worldGen == null || builder == null) { return; }

            object gate = builder.m_lock;
            Heightmap prefab = zoneSystem.m_zonePrefab != null
                ? zoneSystem.m_zonePrefab.GetComponentInChildren<Heightmap>()
                : null;
            if (gate == null || prefab == null) { return; }

            int inFlight;
            lock (gate) { inFlight = builder.m_ready.Count + builder.m_toBuild.Count; }

            int budget = Mathf.Max(0, HeightmapBuilderThroughputPatch.ReadyCapInForce - DistantTileReserve - inFlight);

            Vector2s center = ZoneSystem.GetZone(point);
            LoadingZoneCadencePatch.FillNearRing(zoneSystem, center, zoneSystem.m_simulationDistance, Ring);

            int missing = 0, queued = 0;
            for (int i = 0; i < Ring.Count; i++) {
                // An installed zone has consumed its build already; asking again would queue one
                // that nothing takes.
                if (zoneSystem.m_zones.ContainsKey(Ring[i])) { continue; }

                missing++;
                if (queued >= budget) { continue; }

                // True means it was already finished, so nothing new was queued.
                if (!builder.IsTerrainReady(
                        ZoneSystem.GetZonePos(Ring[i]), prefab.m_width, prefab.m_scale, prefab.IsDistantLod, worldGen)) {
                    queued++;
                }
            }

            Ring.Clear();

            if (missing > 0) {
                Logger.LogDebug(
                    $"Prewarming terrain at the {what} {center}: {queued} of {missing} zone(s) queued for the " +
                    $"build thread, room for {budget} under its ready cap.");
            }
        }
    }
}
