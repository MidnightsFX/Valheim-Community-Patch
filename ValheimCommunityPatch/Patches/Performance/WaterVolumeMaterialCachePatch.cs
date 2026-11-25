using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Water Material Lookup: WaterVolume.UpdateMaterials fetches each tile's material natively every frame.
    // A prefix caches it per volume and checks every 30 frames for a swapped material, re-running SetupMaterial on it.
    // Water costs less each frame, and another mod's frozen or thawed water still animates and colors correctly.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(WaterVolume))]
    internal static class WaterVolumeMaterialCachePatch {
        // ValPerfPatcher's Seasonality hook removes entries from this by name and type; renaming or
        // retyping it makes that mod log a warning at startup.
        private static readonly Dictionary<WaterVolume, Material> Cache = new Dictionary<WaterVolume, Material>();

        // The material each volume's SetupMaterial last ran on. Kept across disable and enable,
        // which do not run Start again; dropped when the volume is destroyed.
        private static readonly Dictionary<WaterVolume, Material> SetUp = new Dictionary<WaterVolume, Material>();

        // Seasonality swaps the material to freeze and thaw water; the old one would get the wave time
        // and the water on screen would stand still while the waves boats ride go on.
        private const int CheckEveryFrames = 30;

        // The game sets s_waterTime once per frame before the UpdateMaterials loop, so a new value
        // marks the first volume of a new frame. NaN never compares equal, so the first call counts.
        private static float _lastWaterTime = float.NaN;
        private static int _framesUntilCheck;
        private static bool _checkThisFrame;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(WaterVolumeMaterialCachePatch));

        [HarmonyPrefix]
        [HarmonyPatch(nameof(WaterVolume.UpdateMaterials))]
        private static bool UpdateMaterialsPrefix(WaterVolume __instance) {
            if (ApiSwitch.Off) { return true; }

            float waterTime = WaterVolume.s_waterTime;
            if (waterTime != _lastWaterTime) {
                _lastWaterTime = waterTime;
                _checkThisFrame = --_framesUntilCheck <= 0;
                if (_checkThisFrame) { _framesUntilCheck = CheckEveryFrames; }
            }

            MeshRenderer renderer = __instance.m_waterSurface;
            if (!Cache.TryGetValue(__instance, out Material material) || material == null ||
                (_checkThisFrame && renderer != null && renderer.sharedMaterial != material)) {
                if (renderer == null) { return true; }

                material = renderer.material;
                if (material == null) { return true; }

                Cache[__instance] = material;

                // A swapped-in material never had the tile's depth and wind written by SetupMaterial,
                // which runs once at Start, so it is run again, as a reload would. Not before the
                // volume's first SetupMaterial: Start runs it on this material.
                if (SetUp.TryGetValue(__instance, out Material setUp) && setUp != material) {
                    __instance.SetupMaterial();
                }
            }

            material.SetFloat(WaterVolume.s_shaderWaterTime, waterTime);
            return false;
        }

        // Vanilla unregisters the volume here; the cache entry goes with it.
        [HarmonyPatch(typeof(WaterVolume), "OnDisable")]
        internal static class DisableHook {
            [HarmonyPostfix]
            private static void Postfix(WaterVolume __instance) => Cache.Remove(__instance);
        }

        // SetupMaterial has just given the renderer its own instance; sharedMaterial returns it
        // without making another.
        [HarmonyPatch(typeof(WaterVolume), "SetupMaterial")]
        internal static class SetupHook {
            [HarmonyPostfix]
            private static void Postfix(WaterVolume __instance) {
                MeshRenderer renderer = __instance.m_waterSurface;
                if (renderer != null) { SetUp[__instance] = renderer.sharedMaterial; }
            }
        }

        [HarmonyPatch(typeof(WaterVolume), "OnDestroy")]
        internal static class DestroyHook {
            [HarmonyPostfix]
            private static void Postfix(WaterVolume __instance) => SetUp.Remove(__instance);
        }
    }
}
