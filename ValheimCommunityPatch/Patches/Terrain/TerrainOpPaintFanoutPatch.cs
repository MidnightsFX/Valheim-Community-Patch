using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // TerrainOp.Awake picks zones by radius, but paint reaches a texel further west and south, missing the neighbour.
    // Postfixes TerrainOp.Awake to apply a paint-only copy of the operation to the extra zones the paint reaches.
    // New edits no longer leave paint stopping dead at zone borders; PaintSeamReconcilePatch repairs older ones.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(TerrainOp))]
    internal static class TerrainOpPaintFanoutPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(TerrainOpPaintFanoutPatch),
                ValConfig.SectionTerrain,
                "Fix Terrain Paint Zone Fanout",
                true,
                "Sends terrain paint to every zone it covers, so paint no longer stops dead along a zone border " +
                "for edits made from now on.");
        }

        private static readonly List<Heightmap> Reached = new List<Heightmap>();
        private static readonly List<Heightmap> PaintReached = new List<Heightmap>();

        [HarmonyPostfix]
        [HarmonyPatch("Awake")]
        private static void AwakePostfix(TerrainOp __instance, bool __runOriginal) {
            // No fan-out to extend if another mod's prefix skipped vanilla's.
            if (!__runOriginal) { return; }

            if (Enabled == null || !Enabled.Value) { return; }
            if (TerrainOp.m_forceDisableTerrainOps) { return; }

            TerrainOp.Settings settings = __instance.m_settings;
            if (settings == null || !settings.m_paintCleared || settings.m_paintRadius <= 0f) { return; }

            Vector3 pos = __instance.transform.position;

            Heightmap local = Heightmap.FindHeightmap(pos);
            float scale = local != null ? local.m_scale : 1f;

            // One texel is the most the floor can shift the kernel centre.
            float vanillaRadius = settings.GetRadius();
            float paintReach = settings.m_paintRadius + scale;
            if (paintReach <= vanillaRadius) { return; }

            Reached.Clear();
            PaintReached.Clear();

            try {
                // Zone lookup is horizontal only, so an operation inside a dungeon (y+5000) would reach the surface
                // below. Unreachable while both TerrainOp creators refuse no-build locations, which every dungeon is;
                // if that loosens, compare the operation's y with the heightmap's before extending to it.
                Heightmap.FindHeightmap(pos, vanillaRadius, Reached);
                Heightmap.FindHeightmap(pos, paintReach, PaintReached);

                // Paint only: level, raise and smooth round to nearest, so vanilla's fan-out already fits them.
                // Masking the shared settings is safe because ApplyOperation serialises them synchronously.
                bool level = settings.m_level;
                bool raise = settings.m_raise;
                bool smooth = settings.m_smooth;

                settings.m_level = false;
                settings.m_raise = false;
                settings.m_smooth = false;

                try {
                    for (int i = 0; i < PaintReached.Count; i++) {
                        Heightmap hmap = PaintReached[i];

                        if (hmap == null || hmap.IsDistantLod) { continue; }
                        if (Reached.Contains(hmap)) { continue; }
                        if (!IsBehindOperation(hmap, pos)) { continue; }

                        hmap.GetAndCreateTerrainCompiler().ApplyOperation(__instance);
                    }
                } finally {
                    settings.m_level = level;
                    settings.m_raise = raise;
                    settings.m_smooth = smooth;
                }
            } catch (Exception ex) {
                Logger.LogWarning($"Could not extend terrain paint past {pos} into neighbouring zones: {ex}");
            } finally {
                Reached.Clear();
                PaintReached.Clear();
            }
        }

        // True when the zone lies on the -x or -z side of the operation, the only direction the
        // floored kernel centre can carry paint past the fan-out radius.
        private static bool IsBehindOperation(Heightmap hmap, Vector3 pos) {
            float half = hmap.m_width * hmap.m_scale * 0.5f;
            Vector3 centre = hmap.transform.position;

            return centre.x + half <= pos.x || centre.z + half <= pos.z;
        }
    }
}
