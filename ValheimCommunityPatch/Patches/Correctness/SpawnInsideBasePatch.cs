using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Spawns Inside Bases: stops creatures spawning on ground a player base covers while the objects
    // that cover it are not loaded.
    //
    // SpawnSystem.IsSpawnPointGood keeps spawns out of a base with EffectArea.IsPointInsideArea, a physics
    // query that only finds objects in the scene. Points are drawn 40 to 80 m from the player, which at the
    // lowest Simulation Distance reaches past the three by three zones whose objects are loaded. Terrain
    // stays for a few seconds after its zone's objects are removed and arrives before they are created, so
    // such a point finds ground and no base, and FindBaseSpawnPoint draws up to 20 points until one passes.
    // A slow game widens the gap, because objects are created a few at a time each frame.
    //
    // A postfix on IsSpawnPointGood turns down a point that passed when its zone is outside the loaded area,
    // where a creature would be unloaded at once, or when a base object with no instance has its area over
    // the point. Those objects come from the ZDOs of the surrounding zones and their areas from the prefabs,
    // so modded pieces count. Points vanilla rejects and spawners allowed inside a base are left alone.
    //
    // A client only holds the ZDOs of zones it has been near, sent nearest first at a capped rate, so it can
    // not see every base object. Close to a base it can see, it also turns down a point within reach of a
    // zone it does not hold yet: one it was never near, or one that came near before new objects last stopped
    // arriving. Away from any base spawning stays as it was.
    //
    // Client: spawning needs a local player.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class SpawnInsideBasePatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SpawnInsideBasePatch),
                ValConfig.SectionCorrectness,
                "Fix Spawns Inside Bases",
                true,
                "Stops creatures spawning on ground your base covers while the workbenches and fires that " +
                "cover it are not loaded. Vanilla only sees loaded objects, so this happens at the lowest " +
                "Simulation Distance while moving through a base, and more often on a slow machine.");
        }

        private struct BaseArea {
            // From the object's position, in its own space.
            public Vector3 Offset;
            public float Radius;
            // Half the straight part of an upright capsule, 0 for a sphere.
            public float HalfSegment;
        }

        private static readonly Dictionary<int, BaseArea[]> AreasByPrefab = new Dictionary<int, BaseArea[]>();
        private static ZNetScene s_tableScene;
        private static int s_tablePrefabCount;
        private static float s_searchRadius;

        // The largest area in the game. A mod's larger one must not close off the whole spawn ring on a client.
        private const float UnheldZoneReach = 30f;
        // Long enough to span gaps in the server's stream, short enough not to hold back normal spawning.
        private const float QuietSeconds = 3f;
        // How far a loaded base area can be from a point for the base to count as carrying on past it.
        private const float BaseNearbyRadius = 64f;

        private static readonly HashSet<Vector2s> HeldZones = new HashSet<Vector2s>();
        private static readonly Dictionary<Vector2s, float> ArrivingZones = new Dictionary<Vector2s, float>();
        private static readonly List<Vector2s> ArrivingScratch = new List<Vector2s>();
        private static Vector2s s_trackedCentre;
        private static bool s_trackedAny;
        private static float s_lastNewObject;

        // Mods add prefabs after ZNetScene.Awake, so the count says when to read them again.
        private static void RefreshTable() {
            ZNetScene scene = ZNetScene.instance;
            if (s_tableScene == scene && s_tablePrefabCount == scene.m_namedPrefabs.Count) { return; }

            // A new session starts holding nothing. A prefab count change alone keeps what is held.
            if (s_tableScene != scene) {
                HeldZones.Clear();
                ArrivingZones.Clear();
                s_trackedAny = false;
            }

            s_tableScene = scene;
            s_tablePrefabCount = scene.m_namedPrefabs.Count;
            AreasByPrefab.Clear();
            s_searchRadius = 0f;

            int queryLayers = LayerMask.GetMask("character_trigger");
            var areas = new List<BaseArea>();
            foreach (KeyValuePair<int, GameObject> entry in scene.m_namedPrefabs) {
                if (entry.Value == null) { continue; }

                areas.Clear();
                foreach (EffectArea area in entry.Value.GetComponentsInChildren<EffectArea>(true)) {
                    if ((area.m_type & EffectArea.Type.PlayerBase) == 0 || !IsActiveInPrefab(area.transform, entry.Value.transform)) { continue; }
                    if ((queryLayers & (1 << area.gameObject.layer)) == 0) { continue; }
                    if (TryReadArea(area, entry.Value.transform, out BaseArea read)) { areas.Add(read); }
                }

                if (areas.Count == 0) { continue; }

                AreasByPrefab[entry.Key] = areas.ToArray();
                foreach (BaseArea read in areas) {
                    s_searchRadius = Mathf.Max(s_searchRadius, read.Radius + new Vector2(read.Offset.x, read.Offset.z).magnitude);
                }
            }
        }

        // A prefab asset is never active in a hierarchy, so the flags are read up to its root.
        private static bool IsActiveInPrefab(Transform transform, Transform root) {
            for (Transform current = transform; current != null && current != root; current = current.parent) {
                if (!current.gameObject.activeSelf) { return false; }
            }
            return true;
        }

        // The game only uses spheres and upright capsules here. A mod's other shape keeps vanilla's answer.
        private static bool TryReadArea(EffectArea area, Transform root, out BaseArea read) {
            read = default;
            Vector3 scale = area.transform.lossyScale;
            switch (area.GetComponent<Collider>()) {
                case SphereCollider sphere when sphere.enabled:
                    read.Offset = root.InverseTransformPoint(area.transform.TransformPoint(sphere.center));
                    read.Radius = sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                    return true;
                case CapsuleCollider capsule when capsule.enabled && capsule.direction == 1:
                    read.Offset = root.InverseTransformPoint(area.transform.TransformPoint(capsule.center));
                    read.Radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
                    read.HalfSegment = Mathf.Max(0f, capsule.height * Mathf.Abs(scale.y) * 0.5f - read.Radius);
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsZoneNear(Vector2s zone) {
            SimulationDistance distance = ZNet.instance.GetSyncedSimulationDistance();
            Vector2s centre = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
            int near = distance.NearSimulationDistance;
            if (distance.IsClassic) { return Mathf.Abs(zone.x - centre.x) <= near && Mathf.Abs(zone.y - centre.y) <= near; }
            return ZoneSystem.instance.ZonesWithinRadius(centre, zone, near);
        }

        private static void TrackNearZones() {
            Vector2s centre = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
            if (!s_trackedAny || centre.x != s_trackedCentre.x || centre.y != s_trackedCentre.y) {
                s_trackedCentre = centre;
                s_trackedAny = true;
                int near = ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance;
                for (int y = centre.y - near; y <= centre.y + near; y++) {
                    for (int x = centre.x - near; x <= centre.x + near; x++) {
                        var zone = new Vector2s(x, y);
                        if (!HeldZones.Contains(zone) && !ArrivingZones.ContainsKey(zone) && IsZoneNear(zone)) { ArrivingZones[zone] = Time.time; }
                    }
                }
            }

            if (ArrivingZones.Count == 0 || Time.time - s_lastNewObject < QuietSeconds) { return; }

            ArrivingScratch.Clear();
            foreach (KeyValuePair<Vector2s, float> zone in ArrivingZones) {
                if (Time.time - zone.Value >= QuietSeconds) { ArrivingScratch.Add(zone.Key); }
            }
            foreach (Vector2s zone in ArrivingScratch) {
                ArrivingZones.Remove(zone);
                HeldZones.Add(zone);
            }
        }

        private static bool IsNearUnheldZone(Vector3 point) {
            TrackNearZones();
            Vector3 reach = new Vector3(UnheldZoneReach, 0f, UnheldZoneReach);
            Vector2s low = ZoneSystem.GetZone(point - reach);
            Vector2s high = ZoneSystem.GetZone(point + reach);
            for (int y = low.y; y <= high.y; y++) {
                for (int x = low.x; x <= high.x; x++) {
                    if (!HeldZones.Contains(new Vector2s(x, y))) { return true; }
                }
            }
            return false;
        }

        private static bool IsBaseNearby(Vector3 point) =>
            EffectArea.IsPointInsideArea(point, EffectArea.Type.PlayerBase, BaseNearbyRadius) != null;

        private static bool IsInside(in BaseArea area, ZDO zdo, Vector3 point) {
            Vector3 centre = zdo.GetPosition() + zdo.GetRotation() * area.Offset;
            float y = Mathf.Clamp(point.y, centre.y - area.HalfSegment, centre.y + area.HalfSegment);
            float dx = point.x - centre.x, dy = point.y - y, dz = point.z - centre.z;
            return dx * dx + dy * dy + dz * dz <= area.Radius * area.Radius;
        }

        private static bool AnyUnloadedAreaOver(List<ZDO> zdos, Vector3 point) {
            if (zdos == null) { return false; }

            for (int i = 0; i < zdos.Count; i++) {
                ZDO zdo = zdos[i];
                if (!AreasByPrefab.TryGetValue(zdo.GetPrefab(), out BaseArea[] areas)) { continue; }

                for (int a = 0; a < areas.Length; a++) {
                    // With an instance the physics query has already answered for this object.
                    if (IsInside(areas[a], zdo, point) && !ZNetScene.instance.HaveInstance(zdo)) { return true; }
                }
            }
            return false;
        }

        private static bool IsUnderUnloadedBase(Vector3 point) {
            RefreshTable();
            Vector3 reach = new Vector3(s_searchRadius, 0f, s_searchRadius);
            Vector2s low = ZoneSystem.GetZone(point - reach);
            Vector2s high = ZoneSystem.GetZone(point + reach);
            ZDOMan zdoMan = ZDOMan.instance;
            for (int y = low.y; y <= high.y; y++) {
                for (int x = low.x; x <= high.x; x++) {
                    ZoneSystem.SectorIndex sector = ZoneSystem.SectorToIndex(x, y);
                    if (AnyUnloadedAreaOver(zdoMan.m_objectsBySector[sector.Sector], point)) { return true; }
                    if (zdoMan.m_portalObjects.TryGetValue(sector, out List<ZDO> portals) && AnyUnloadedAreaOver(portals, point)) { return true; }
                }
            }
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SpawnSystem), nameof(SpawnSystem.IsSpawnPointGood))]
        private static void IsSpawnPointGoodPostfix(SpawnSystem.SpawnData spawn, ref Vector3 spawnPoint, ref bool __result) {
            if (!__result || spawn.m_insidePlayerBase || !Enabled.Value) { return; }

            if (!IsZoneNear(ZoneSystem.GetZone(spawnPoint)) || IsUnderUnloadedBase(spawnPoint)) { __result = false; }
            else if (!ZNet.instance.IsServer() && IsNearUnheldZone(spawnPoint) && IsBaseNearby(spawnPoint)) { __result = false; }
        }

        // Runs every second for each loaded zone, so a client's view of what it holds stays current between
        // spawn attempts, and the table is read near the start of a session rather than mid-play.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
        private static void UpdateSpawningPrefix() {
            if (!Enabled.Value || ZNet.instance == null || ZNetScene.instance == null) { return; }

            RefreshTable();
            if (!ZNet.instance.IsServer()) { TrackNearZones(); }
        }

        // Counted here and not in AddToSector, which also fires for the game's own short-lived local objects.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
        private static void ReceivePrefix(ZDOMan __instance, out int __state) {
            __state = __instance.m_objectsByID.Count;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
        private static void ReceivePostfix(ZDOMan __instance, int __state) {
            if (__instance.m_objectsByID.Count > __state) { s_lastNewObject = Time.time; }
        }
    }
}
