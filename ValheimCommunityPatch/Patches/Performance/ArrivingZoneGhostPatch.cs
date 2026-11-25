using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Arriving Location Hitch: a host reaching an ungenerated location zone builds all of it in one frame.
    // Prefixes ZoneSystem.PokeLocalZone to generate that zone in ghost mode first, so the next tick loads it as land.
    // Its objects, location model and dungeon rooms then arrive a few per frame, within the location and room budgets.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZoneSystem), "PokeLocalZone")]
    internal static class ArrivingZoneGhostPatch {
        private const string FixName = "Fix Arriving Location Hitch";

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(ArrivingZoneGhostPatch));

        private static readonly TakeoverCheck PokeTakeover = new TakeoverCheck(
            typeof(ArrivingZoneGhostPatch),
            AccessTools.DeclaredMethod(typeof(ZoneSystem), "PokeLocalZone"),
            transpilers: true,
            owners => $"Zone loading is changed by {owners}, so '{FixName}' stands down and arriving zones " +
                      "generate as that mod decides.");

        private static readonly TakeoverCheck SpawnTakeover = new TakeoverCheck(
            typeof(ArrivingZoneGhostPatch),
            AccessTools.DeclaredMethod(typeof(ZoneSystem), "SpawnZone"),
            transpilers: true,
            owners => $"Zone generation is changed by {owners}, so '{FixName}' stands down and arriving zones " +
                      "generate in full.");

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(ZoneSystem __instance, Vector2s zoneID, ref bool __result, bool __runOriginal) {
            if (!__runOriginal || ApiSwitch.Off || !GenerateFirst(__instance, zoneID, out string location)) { return true; }

            // Fails, as the full spawn would, while the terrain or the location prefab is still
            // loading; PokeLocalZone then reports nothing done, and the next tick asks again. The
            // world and its dungeon layouts come out as a ghost-ring generation's, and the scene
            // creates no nearby object while a nearby zone is missing, so these background copies
            // are gone before real ones exist.
            long start = Stopwatch.GetTimestamp();
            __result = __instance.SpawnZone(zoneID, ZoneSystem.SpawnMode.Ghost, out GameObject _);

            // This frame's world-building budget is spent: location models and dungeon rooms already
            // waiting from earlier zones hold off until the next frame instead of piling on.
            long elapsed = Stopwatch.GetTimestamp() - start;
            LocationSpawnPacingPatch.ChargeFrame(elapsed);
            DungeonRoomSpreadPatch.ChargeFrame(elapsed);

            if (__result && Logger.DebugEnabled) {
                Logger.LogDebug($"{FixName}: zone {zoneID.x},{zoneID.y} with {location} generated in the background; it loads next tick.");
            }

            return false;
        }

        private static bool GenerateFirst(ZoneSystem zoneSystem, Vector2s zoneID, out string location) {
            location = null;

            ZNet znet = ZNet.instance;
            if (znet == null || !znet.IsServer() || Player.m_localPlayer == null || RunMode.InLoadingScreen()) { return false; }

            // Installed already (vanilla only refreshes it), or generated already (vanilla loads it).
            if (zoneSystem.m_zones.ContainsKey(zoneID) || zoneSystem.IsZoneGenerated(zoneID)) { return false; }

            if (!zoneSystem.m_locationInstances.TryGetValue(zoneID, out ZoneSystem.LocationInstance instance) || instance.m_placed) {
                return false;
            }

            // The ground under the player has to exist this tick.
            if (zoneID == ZoneSystem.GetZone(znet.GetReferencePosition())) { return false; }

            if (PokeTakeover.TakenOver || SpawnTakeover.TakenOver) { return false; }

            location = instance.m_location?.m_prefabName ?? "a location";
            return true;
        }
    }
}
