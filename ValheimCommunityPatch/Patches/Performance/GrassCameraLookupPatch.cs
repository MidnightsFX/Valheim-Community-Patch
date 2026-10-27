using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Grass Camera Lookups: each grass renderer's per-frame visibility check and fade distance
    // read the camera, its view planes and its position from one lookup per frame instead of asking
    // the engine again for every renderer.
    //
    // ClutterSystem gives every instanced clutter type in every grass patch its own InstanceRenderer,
    // a few hundred around the player and well over a thousand with a raised grass distance, and
    // MonoUpdaters calls CustomUpdate on each one every frame. Each call asks the engine for the frame
    // number up to three times to find the camera and its cached view planes, and each one in view
    // reads the camera's transform and position again for its fade distance. Two thirds of them are
    // out of view and draw nothing, so most of that is the lookup alone.
    //
    // A MonoUpdaters.Update prefix reads the camera, Utils' view planes and the camera position once
    // per pass, and a CustomUpdate prefix runs vanilla's body against them: the same sphere-against-
    // planes test, fade thinning and draw call, in the same order. Calls from outside that pass, and
    // a CustomUpdate another mod can skip or rewrite, keep vanilla's own lookups.
    //
    // Client: grass is only drawn where there is a camera.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(InstanceRenderer))]
    internal static class GrassCameraLookupPatch {
        private const string FixName = "Fix Grass Camera Lookups";

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(GrassCameraLookupPatch));

        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            typeof(GrassCameraLookupPatch),
            AccessTools.DeclaredMethod(typeof(InstanceRenderer), nameof(InstanceRenderer.CustomUpdate)),
            transpilers: true,
            owners => $"Grass drawing is changed by {owners}, so '{FixName}' stands down and that mod's " +
                      "version applies.");

        // Set for the length of one MonoUpdaters.Update, which is where vanilla runs every renderer.
        private static bool _inPass;
        private static bool _haveCamera;
        private static Plane[] _planes;
        private static Vector3 _cameraPosition;

        [HarmonyPatch(typeof(MonoUpdaters), "Update")]
        internal static class PassHook {
            [HarmonyPrefix]
            private static void Prefix() {
                // Utils caches the planes per frame itself, so these are the ones vanilla's check
                // would compute at its first call; nothing in the pass moves the camera.
                Camera camera = Utils.GetMainCamera();
                _haveCamera = camera != null;
                _planes = _haveCamera ? Utils.GetMainCameraFrustumPlanes() : null;
                _cameraPosition = _haveCamera ? camera.transform.position : Vector3.zero;
                _inPass = true;
            }

            [HarmonyFinalizer]
            private static void Finalizer() {
                _inPass = false;
                _planes = null;
            }
        }

        // Vanilla's CustomUpdate with the camera reads taken from the pass.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(nameof(InstanceRenderer.CustomUpdate))]
        private static bool CustomUpdatePrefix(InstanceRenderer __instance, float deltaTime, bool __runOriginal) {
            if (!__runOriginal) { return false; }
            if (!_inPass || ApiSwitch.Off || Takeover.TakenOver) { return true; }

            if (__instance.m_instanceCount == 0 || !_haveCamera) { return false; }

            if (__instance.m_frustumCull) {
                if (__instance.m_dirtyBounds) { __instance.UpdateBounds(); }
                if (!InsideCamera(__instance.m_bounds)) { return false; }
            }

            if (!__instance.m_useLod) {
                Draw(__instance, __instance.m_instanceCount);
                return false;
            }

            Vector3 position = __instance.transform.position;
            float distance = __instance.m_useXZLodDistance
                ? Utils.DistanceXZ(_cameraPosition, position)
                : Vector3.Distance(_cameraPosition, position);
            int target = (int)((1f - Utils.LerpStep(__instance.m_lodMinDistance, __instance.m_lodMaxDistance, distance)) * (float)__instance.m_instanceCount);
            float maxDelta = deltaTime * (float)__instance.m_instanceCount;
            __instance.m_lodCount = Mathf.MoveTowards(__instance.m_lodCount, target, maxDelta);
            if (__instance.m_firstFrame) {
                if (distance < __instance.m_lodMinDistance) { __instance.m_lodCount = target; }
                __instance.m_firstFrame = false;
            }

            __instance.m_lodCount = Mathf.Min(__instance.m_lodCount, __instance.m_instanceCount);
            int count = (int)__instance.m_lodCount;
            if (count > 0) { Draw(__instance, count); }

            return false;
        }

        // Utils.InsideMainCamera against the planes read for this pass.
        private static bool InsideCamera(BoundingSphere bounds) {
            Plane[] planes = _planes;
            if (planes == null) { return false; }

            for (int i = 0; i < planes.Length; i++) {
                if (planes[i].GetDistanceToPoint(bounds.position) < 0f - bounds.radius) { return false; }
            }

            return true;
        }

        private static void Draw(InstanceRenderer renderer, int count) {
            Graphics.DrawMeshInstanced(
                renderer.m_mesh, 0, renderer.m_material, renderer.m_instances, count, null,
                renderer.m_shadowCasting, true, InstanceRenderer.s_layer);
        }
    }
}
