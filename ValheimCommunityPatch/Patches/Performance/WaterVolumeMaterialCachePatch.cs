using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Water Material Lookup: each water tile caches its surface material instead of fetching
    // it from the engine every frame.
    //
    // WaterVolume.UpdateMaterials runs for every loaded water volume every frame and is one line,
    // m_waterSurface.material.SetFloat(...). Renderer.material is a native call that returns the
    // same instance until something assigns the renderer a new material.
    //
    // A prefix caches the material per volume on first use and writes the water time through the
    // cache. Every 30 frames it checks that the surface still shows the cached material, and
    // re-fetches it if another mod has swapped it (Seasonality freezes and thaws water this way);
    // otherwise the old material would get the wave time and the water on screen would stand still
    // while the waves boats ride go on. A swapped-in material also never had the tile's depth and
    // wind written to it by SetupMaterial, which runs once when the tile starts, so it would be
    // colored differently from tiles that loaded with it: the volume's SetupMaterial is run again
    // on it, as a reload would. The entry is dropped when the volume disables, and a destroyed
    // material is re-fetched once.
    //
    // Client: material state is rendering state.
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

                // Not before the volume's first SetupMaterial: Start runs it on this material.
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
