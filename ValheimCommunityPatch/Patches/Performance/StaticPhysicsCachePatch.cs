using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: StaticPhysics - the component on every tree, rock and placed prop that checks
    // whether the ground under it disappeared - re-reads this.transform and this.transform.position
    // around six times per check across SUpdate/CheckFall/GetFallHeight/PushUp, each a native
    // interop call, and answers "how high is the terrain here" with a 10 km Physics.Raycast
    // (ZoneSystem.GetGroundHeight) even though for the common no-solids case the answer is a pure
    // data lookup in the heightmap the ray would hit.
    //
    // SlowUpdater drives 100 SUpdate calls per frame continuously (SlowUpdater.cs:28), and every
    // StaticPhysics schedules its first check 20 seconds after Awake - so a zone-generation burst
    // turns into a delayed wave of raycasts. Profiling attributed ~81 seconds of a day-long session
    // to these checks, concentrated after fresh generation.
    //
    // Fix, part one (always on with the toggle): reimplement SUpdate with the transform fetched once
    // and its position read once, threaded through the same logic. Order, thresholds and side
    // effects mirror vanilla exactly, including PushUp still running in the tick whose CheckFall
    // just started a fall.
    //
    // Fix, part two (advanced, off by default): for m_checkSolids == false objects, replace the
    // GetGroundHeight raycast with the height of the collision surface computed from heightmap data:
    // same triangle split as RebuildCollisionMesh (the quad's B-D anti-diagonal, Heightmap.cs:
    // 487-502), so it evaluates the same surface the ray would hit, without PhysX. m_checkSolids
    // objects keep the raycast - GetSolidHeight genuinely needs to see rocks and buildings. The
    // falling path (FallUpdate) is untouched either way; it is movement, not a check.
    //
    // Both: dedicated servers run StaticPhysics for the objects around world origin and for whatever
    // vanilla simulates server-side; the check cost is the same wherever it runs.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(StaticPhysics))]
    internal static class StaticPhysicsCachePatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> UseHeightmapData;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(StaticPhysicsCachePatch),
                ValConfig.SectionPerformance,
                "Fix Static Object Ground Checks",
                true,
                "Reads each static object's position once per ground check instead of several times " +
                "through the engine. Trees and rocks re-check their ground continuously, so the " +
                "redundant native reads add up after zones generate.");

            UseHeightmapData = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Static Ground Checks Use Heightmap Data",
                false,
                "Answers static objects' terrain-height checks from heightmap data instead of a " +
                "physics raycast. Evaluates the same surface the ray would hit, without the physics " +
                "engine. Off by default for one release while it soaks; requires 'Fix Static Object " +
                "Ground Checks'.",
                advanced: true);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(StaticPhysics.SUpdate))]
        private static bool SUpdatePrefix(StaticPhysics __instance, float time, Vector2i referenceZone) {
            if (Enabled == null || !Enabled.Value) { return true; }

            // Vanilla's gate order: falling, ShouldUpdate (time > m_updateTime), active area.
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

            // Vanilla's Fall(): sets m_falling and starts the FallUpdate InvokeRepeating loop.
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

        // The terrain-height question, answered vanilla's way or from data per the advanced toggle.
        private static bool GroundHeight(Vector3 position, out float height) {
            if (UseHeightmapData == null || !UseHeightmapData.Value) {
                return ZoneSystem.instance.GetGroundHeight(position, out height);
            }

            // Registry path first: cached origin, no native reads. Fallback when it cannot serve.
            Heightmap hmap;
            Vector3 origin;
            if (!HeightmapLookupPatch.TryGetCached(position, out hmap, out origin)) {
                hmap = Heightmap.FindHeightmap(position);
                origin = hmap != null ? hmap.transform.position : Vector3.zero;
            }

            if (hmap == null) {
                // No loaded heightmap is also where the raycast would have found no terrain
                // collider; mirror vanilla's miss result.
                height = 0f;
                return false;
            }

            // Same surface the raycast would hit - see HeightmapSampling for the triangulation.
            return HeightmapSampling.TryGetHeight(hmap, origin, position, out height);
        }
    }
}
