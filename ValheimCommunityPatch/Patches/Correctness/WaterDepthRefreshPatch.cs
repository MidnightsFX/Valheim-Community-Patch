using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Unbuilt Water Depth: a water tile that read its depth before its terrain was built reads it
    // again once the terrain is, instead of staying flat until the zone next loads.
    //
    // WaterVolume.Start reads the sea depth at the four corners of its heightmap once. Vanilla always
    // has them by then, because a heightmap builds inside its OnEnable. A mod can put that build off
    // to a later frame (Fires Ghetto Networking's Spread Terrain Rebuilds does), and a tile that starts
    // in the gap reads four zeros. Depth scales wave height, so the tile draws flat water with the
    // waves stopping at its border, boats and swimmers ride it flat, and m_oneDepth holds the zero
    // even through a later read, because DetectWaterDepth only ever sets it.
    //
    // A DetectWaterDepth postfix notes a tile that read an unbuilt heightmap, and an UpdateCornerDepths
    // postfix gives it a fresh read and material when that heightmap's corners are written. Only tiles
    // that read too early are touched: a corner depth changed by a later terrain edit still reaches the
    // water when the zone next loads, as in vanilla. Other mods' DetectWaterDepth and SetupMaterial
    // postfixes run again on the fresh read, so they apply to the true depth.
    //
    // Client: a dedicated server has no use for putting off terrain meshes, and draws no water.
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
                "Water tiles that load before their terrain is built read their depth again once it is. " +
                "Without this, when another mod spreads terrain building over several frames, a tile " +
                "can stay flat until its zone next loads, with the waves stopping at its border. Does " +
                "nothing unless another mod delays terrain building this way.");
        }

        [HarmonyPostfix]
        [HarmonyPatch("DetectWaterDepth")]
        private static void DetectWaterDepthPostfix(WaterVolume __instance) {
            Heightmap hmap = __instance.m_heightmap;
            if (hmap == null || HeightmapSampling.IsBuilt(hmap)) { return; }
            if (Enabled == null || !Enabled.Value || Waiting.Contains(__instance)) { return; }

            Waiting.Add(__instance);
        }

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
            // zero from the early read would otherwise outlive a real depth on a shore tile.
            private static void Refresh(WaterVolume volume) {
                volume.m_oneDepth = -1f;
                volume.DetectWaterDepth();
                volume.SetupMaterial();
            }
        }
    }
}
