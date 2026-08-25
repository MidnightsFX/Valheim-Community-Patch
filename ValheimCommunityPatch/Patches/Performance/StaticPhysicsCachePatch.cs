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

            Heightmap hmap = Heightmap.FindHeightmap(position);
            if (hmap == null) {
                // No loaded heightmap is also where the raycast would have found no terrain
                // collider; mirror vanilla's miss result.
                height = 0f;
                return false;
            }

            return InterpolatedHeight(hmap, position, out height);
        }

        // The exact height of the collision mesh at (x,z): bilinear per triangle with the same
        // B-D anti-diagonal split RebuildCollisionMesh indexes (A=(x,y) D=(x,y+1) B=(x+1,y) then
        // B D C=(x+1,y+1)), so this evaluates the surface the raycast would hit.
        private static bool InterpolatedHeight(Heightmap hmap, Vector3 position, out float height) {
            Vector3 origin = hmap.transform.position;
            int width = hmap.m_width;
            float scale = hmap.m_scale;

            float fx = (position.x - origin.x) / scale + width * 0.5f;
            float fz = (position.z - origin.z) / scale + width * 0.5f;
            if (fx < 0f || fx > width || fz < 0f || fz > width) {
                height = 0f;
                return false;
            }

            int x0 = Mathf.Min((int)fx, width - 1);
            int z0 = Mathf.Min((int)fz, width - 1);
            float rx = fx - x0;
            float rz = fz - z0;

            float h00 = hmap.GetHeight(x0, z0);
            float h10 = hmap.GetHeight(x0 + 1, z0);
            float h01 = hmap.GetHeight(x0, z0 + 1);
            float h11 = hmap.GetHeight(x0 + 1, z0 + 1);

            float local = rx + rz <= 1f
                ? h00 + (h10 - h00) * rx + (h01 - h00) * rz
                : h11 + (h01 - h11) * (1f - rx) + (h10 - h11) * (1f - rz);

            height = local + origin.y;
            return true;
        }
    }
}
