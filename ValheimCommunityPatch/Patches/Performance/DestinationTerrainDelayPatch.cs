using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Destination Terrain Delay: destination terrain is first requested on arrival, so loading screens wait for it.
    // Postfixes Player.TeleportTo and Game.RequestRespawn to queue the destination's missing zones early.
    // Loading screens after portals and deaths are shorter, since the ground is often built before arrival.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class DestinationTerrainDelayPatch {
        // TerrainLod's ring: queued together once the camera has moved, and held in the ready list
        // until all nine are built.
        private const int DistantTileReserve = 9;

        private static readonly List<Vector2s> Ring = new List<Vector2s>();

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(DestinationTerrainDelayPatch));

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
            if (ApiSwitch.Off) { return; }

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

            // Finished results past the ready cap are evicted oldest first, so stop short of it.
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

                // Only queues a build, with SpawnZone's own arguments, so SpawnZone later finds the result
                // waiting. True means it was already finished, so nothing new was queued.
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
