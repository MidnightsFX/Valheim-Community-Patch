using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: WaterVolume.UpdateMaterials runs for every loaded water volume every frame
    // and is a single line (WaterVolume.cs:118-121):
    //
    //   this.m_waterSurface.material.SetFloat(WaterVolume.s_shaderWaterTime, WaterVolume.s_waterTime);
    //
    // Renderer.get_material is a native engine call per volume per frame - it instantiated the
    // per-renderer material copy on first access and afterwards just fetches it, but the fetch
    // is interop that answers the same reference every time for the volume's whole life.
    // Profiling attributed ~1.4 seconds of a 10-minute coastal session to that fetch alone,
    // alongside the SetFloat write it feeds (which is a genuine per-material update of the
    // advancing water time, and stays).
    //
    // Fix: cache the material instance per volume on first use and write through the cache. The
    // entry is dropped when the volume disables - the same hook vanilla uses to unregister it -
    // and a Unity-destroyed material (scene teardown races) re-fetches once. Every other
    // .material user (SetupMaterial's one-time setup) stays vanilla and shares the same
    // per-renderer instance, so nothing can diverge.
    //
    // Client: material state is rendering state; a dedicated server is never patched here and
    // keeps vanilla's writes that nothing ever draws.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(WaterVolume))]
    internal static class WaterVolumeMaterialCachePatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(WaterVolumeMaterialCachePatch),
                ValConfig.SectionPerformance,
                "Fix Water Material Lookup",
                true,
                "Caches each water surface's material instead of re-fetching it from the engine " +
                "every frame for every loaded water tile. The per-frame water time update itself " +
                "is unchanged.");
        }

        private static readonly Dictionary<WaterVolume, Material> Cache = new Dictionary<WaterVolume, Material>();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(WaterVolume.UpdateMaterials))]
        private static bool UpdateMaterialsPrefix(WaterVolume __instance) {
            if (Enabled == null || !Enabled.Value) { return true; }

            if (!Cache.TryGetValue(__instance, out Material material) || material == null) {
                MeshRenderer renderer = __instance.m_waterSurface;
                if (renderer == null) { return true; }

                material = renderer.material;
                Cache[__instance] = material;
            }

            material.SetFloat(WaterVolume.s_shaderWaterTime, WaterVolume.s_waterTime);
            return false;
        }

        // Vanilla unregisters the volume here (WaterVolume.cs:75); the cache entry goes with it.
        [HarmonyPatch(typeof(WaterVolume), "OnDisable")]
        internal static class DisableHook {
            [HarmonyPostfix]
            private static void Postfix(WaterVolume __instance) => Cache.Remove(__instance);
        }
    }
}
