using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Static Object Ground Checks: StaticPhysics.SUpdate re-reads the transform about six times per ground check.
    // A prefix replaces SUpdate with the same logic, order and thresholds, fetching the transform and position once.
    // The 100 tree and rock ground checks run each frame make far fewer native calls.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(StaticPhysics))]
    internal static class StaticPhysicsCachePatch {
        private const string FixName = "Fix Static Object Ground Checks";

        internal static ConfigEntry<bool> UseHeightmapData;

        private static bool _loggedStandDown;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(StaticPhysicsCachePatch));

        internal static void BindConfig() {
            UseHeightmapData = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Static Ground Checks Use Heightmap Data",
                false,
                "Answers static objects' terrain-height checks from heightmap data, the same surface the " +
                "ray would hit, instead of a physics raycast. Off keeps the game's raycast.",
                advanced: true);
        }

        [HarmonyPrepare]
        private static bool Prepare() {
            if (!HearthBelowCompat.Loaded) { return true; }

            if (!_loggedStandDown) {
                _loggedStandDown = true;
                Logger.LogInfo(
                    $"HearthBelow is loaded, so '{FixName}' stands down: HearthBelow keeps objects in " +
                    "dug-out ground from being pushed up through the game's ground check, which this fix " +
                    "replaces. The game's ground check runs.");
            }

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(StaticPhysics.SUpdate))]
        private static bool SUpdatePrefix(StaticPhysics __instance, float time, Vector2s referenceZone) {
            if (ApiSwitch.Off) { return true; }

            // Vanilla's gate order: falling, ShouldUpdate, active area.
            if (__instance.m_falling || time <= __instance.m_updateTime) { return false; }

            Transform transform = __instance.transform;
            Vector3 position = transform.position;

            if (ZNetScene.OutsideActiveArea(position, referenceZone)) { return false; }

            if (__instance.m_fall) { CheckFall(__instance, transform, position); }

            if (__instance.m_pushUp) { PushUp(__instance, transform, position); }

            return false;
        }

        private static void CheckFall(StaticPhysics sp, Transform transform, Vector3 position) {
            if (position.y <= GetFallHeight(sp, transform, position) + 0.05f) { return; }

            sp.Fall();
        }

        // GetSolidHeight keeps its raycast whatever the heightmap setting: it has to see rocks and buildings.
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
