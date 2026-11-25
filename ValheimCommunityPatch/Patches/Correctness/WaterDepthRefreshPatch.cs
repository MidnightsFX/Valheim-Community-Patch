using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Water reads its depth once at Start, so a tile whose terrain a mod builds late stays flat until the zone reloads.
    // Postfixes WaterVolume.DetectWaterDepth to note such tiles and Heightmap.UpdateCornerDepths to read them again.
    // Water beside late-built terrain gets its real waves instead of a flat tile with waves stopping at its edge.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(WaterVolume))]
    internal static class WaterDepthRefreshPatch {
        internal static FixToggle Enabled;

        // Empty unless a mod is putting builds off, and then one tile per zone being waited on, so
        // the corner depth hook pays one Count check in the ordinary case.
        private static readonly List<WaterVolume> Waiting = new List<WaterVolume>();

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(WaterDepthRefreshPatch),
                ValConfig.SectionCorrectness,
                "Fix Unbuilt Water Depth",
                true,
                "Makes water tiles that load before another mod has built their terrain read their depth again " +
                "once it is built, instead of staying flat.");
        }

        // Vanilla builds a heightmap inside its OnEnable, so only a mod putting that off to a later frame
        // (Fires Ghetto Networking's Spread Terrain Rebuilds does) leaves a tile reading four zeros here.
        [HarmonyPostfix]
        [HarmonyPatch("DetectWaterDepth")]
        private static void DetectWaterDepthPostfix(WaterVolume __instance) {
            Heightmap hmap = __instance.m_heightmap;
            if (hmap == null || HeightmapSampling.IsBuilt(hmap)) { return; }
            if (Enabled == null || !Enabled.Value || Waiting.Contains(__instance)) { return; }

            Waiting.Add(__instance);
        }

        // Only tiles that read too early are refreshed: a corner depth changed by a later terrain edit
        // still reaches the water when the zone next loads, as in vanilla.
        [HarmonyPatch(typeof(Heightmap), "UpdateCornerDepths")]
        internal static class CornerDepthsHook {
            [HarmonyPostfix]
            private static void Postfix(Heightmap __instance) {
                if (Waiting.Count == 0) { return; }

                for (int i = Waiting.Count - 1; i >= 0; i--) {
                    WaterVolume volume = Waiting[i];

                    // Gone with its zone while waiting: nothing else clears these.
                    if (volume == null || volume.m_heightmap == null) {
                        Waiting.RemoveAt(i);
                        continue;
                    }

                    if (!ReferenceEquals(volume.m_heightmap, __instance)) { continue; }

                    Waiting.RemoveAt(i);
                    if (Enabled.Value) { Refresh(volume); }
                }
            }

            // m_oneDepth first: DetectWaterDepth keeps it unless all four corners match, so the
            // zero from the early read would otherwise outlive a real depth on a shore tile. Calling
            // the game's methods runs other mods' postfixes on them again, on the true depth.
            private static void Refresh(WaterVolume volume) {
                volume.m_oneDepth = -1f;
                volume.DetectWaterDepth();
                volume.SetupMaterial();
            }
        }
    }
}
