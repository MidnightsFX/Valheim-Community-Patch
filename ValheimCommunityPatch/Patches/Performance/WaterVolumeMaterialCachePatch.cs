using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Water Material Lookup: each water tile caches its surface material instead of fetching
    // it from the engine every frame.
    //
    // WaterVolume.UpdateMaterials runs for every loaded water volume every frame and is one line,
    // m_waterSurface.material.SetFloat(...). Renderer.material is a native call that returns the
    // same instance for the volume's whole life.
    //
    // A prefix caches the material per volume on first use and writes the water time through the
    // cache. The entry is dropped when the volume disables, and a destroyed material is re-fetched
    // once. Everything else that touches the material keeps sharing the same per-renderer instance.
    //
    // Client: material state is rendering state.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(WaterVolume))]
    internal static class WaterVolumeMaterialCachePatch {
        private static readonly Dictionary<WaterVolume, Material> Cache = new Dictionary<WaterVolume, Material>();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(WaterVolume.UpdateMaterials))]
        private static bool UpdateMaterialsPrefix(WaterVolume __instance) {
            if (!Cache.TryGetValue(__instance, out Material material) || material == null) {
                MeshRenderer renderer = __instance.m_waterSurface;
                if (renderer == null) { return true; }

                material = renderer.material;
                Cache[__instance] = material;
            }

            material.SetFloat(WaterVolume.s_shaderWaterTime, WaterVolume.s_waterTime);
            return false;
        }

        // Vanilla unregisters the volume here; the cache entry goes with it.
        [HarmonyPatch(typeof(WaterVolume), "OnDisable")]
        internal static class DisableHook {
            [HarmonyPostfix]
            private static void Postfix(WaterVolume __instance) => Cache.Remove(__instance);
        }
    }
}
