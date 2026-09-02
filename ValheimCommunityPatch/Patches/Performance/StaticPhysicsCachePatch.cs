using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Static Object Ground Checks: a tree or rock's ground check reads its transform once
    // instead of about six times, and can answer the terrain height from heightmap data.
    //
    // StaticPhysics.SUpdate and the methods it calls re-read this.transform and
    // transform.position about six times per check, each a native call, and answer "how high is
    // the terrain here" with a 10 km Physics.Raycast through ZoneSystem.GetGroundHeight.
    // SlowUpdater drives 100 of these checks per frame continuously.
    //
    // A prefix replaces SUpdate with the same logic, order and thresholds, with the transform and
    // position fetched once. An advanced, default-off toggle further answers the terrain height
    // for objects that do not check solids from heightmap data (HeightmapSampling evaluates the
    // same surface the ray would hit). Objects with m_checkSolids keep the raycast, because
    // GetSolidHeight has to see rocks and buildings. The falling path is untouched.
    //
    // Both: a dedicated server runs StaticPhysics for its own active area.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(StaticPhysics))]
    internal static class StaticPhysicsCachePatch {
        internal static ConfigEntry<bool> UseHeightmapData;

        internal static void BindConfig() {
            UseHeightmapData = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Static Ground Checks Use Heightmap Data",
                false,
                "Answers static objects' terrain-height checks from heightmap data instead of a " +
                "physics raycast. Evaluates the same surface the ray would hit, without the physics " +
                "engine. Off by default for one release while it soaks.",
                advanced: true);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(StaticPhysics.SUpdate))]
        private static bool SUpdatePrefix(StaticPhysics __instance, float time, Vector2i referenceZone) {
            // Vanilla's gate order: falling, ShouldUpdate, active area.
            if (__instance.m_falling || time <= __instance.m_updateTime) { return false; }

            Transform transform = __instance.transform;
            Vector3 position = transform.position;

            if (ZNetScene.OutsideActiveArea(position, referenceZone, __instance.m_activeArea)) { return false; }

            if (__instance.m_fall) { CheckFall(__instance, transform, position); }

            if (__instance.m_pushUp) { PushUp(__instance, transform, position); }

            return false;
        }

        private static void CheckFall(StaticPhysics sp, Transform transform, Vector3 position) {
            if (position.y <= GetFallHeight(sp, transform, position) + 0.05f) { return; }

            sp.Fall();
        }

        private static float GetFallHeight(StaticPhysics sp, Transform transform, Vector3 position) {
            float height;
            if (sp.m_checkSolids) {
                return ZoneSystem.instance.GetSolidHeight(position, sp.m_fallCheckRadius, out height, transform)
                    ? height
                    : position.y;
            }

            return GroundHeight(position, out height) ? height : position.y;
        }

        private static void PushUp(StaticPhysics sp, Transform transform, Vector3 position) {
            float height;
            if (!GroundHeight(position, out height) || position.y >= height - 0.05f) { return; }

            GameObject gameObject = sp.gameObject;
            gameObject.isStatic = false;
            position.y = height;
            transform.position = position;
            gameObject.isStatic = true;

            ZNetView nview = sp.m_nview;
            if (!(bool)(Object)nview || !nview.IsValid() || !nview.IsOwner()) { return; }

            nview.GetZDO().SetPosition(position);
        }

        private static bool GroundHeight(Vector3 position, out float height) {
            if (UseHeightmapData == null || !UseHeightmapData.Value) {
                return ZoneSystem.instance.GetGroundHeight(position, out height);
            }

            Heightmap hmap;
            Vector3 origin;
            if (!HeightmapLookupPatch.TryGetCached(position, out hmap, out origin)) {
                hmap = Heightmap.FindHeightmap(position);
                origin = hmap != null ? hmap.transform.position : Vector3.zero;
            }

            // No loaded heightmap is where the raycast would also have missed.
            if (hmap == null) {
                height = 0f;
                return false;
            }

            return HeightmapSampling.TryGetHeight(hmap, origin, position, out height);
        }
    }
}
