using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Fix Terrain Paint Zone Fanout: terrain paint is recorded into every zone it actually
    // covers, so the two sides of a zone border stop disagreeing.
    //
    // TerrainOp.Awake decides which zones an operation touches from GetRadius() alone, but
    // TerrainComp.PaintCleared shifts the position half a texel and floors it, which snaps the
    // paint kernel up to a full texel toward -x/-z. An operation near a zone's west or south
    // border therefore paints that zone's own copy of the shared boundary texel while the
    // neighbour, never included in the fan-out, keeps the unpainted copy. Only paint is affected:
    // level, raise and smooth round to nearest and stay symmetric.
    //
    // A postfix on Awake finds the extra zones the paint kernel reaches on the -x/-z side and
    // hands them a paint-only copy of the operation (the settings object is masked around the
    // call, which is safe because ApplyOperation serialises it synchronously). This changes what
    // is saved, for operations placed from now on; PaintSeamReconcilePatch repairs borders that
    // already diverged.
    //
    // Zone lookup is horizontal only, so a TerrainOp created inside a dungeon (built at y+5000)
    // would resolve to the surface heightmaps beneath it. Unreachable today because both TerrainOp
    // creators refuse inside no-build locations, which every dungeon is; if that gate ever
    // loosens, compare the operation's y against the heightmap's before extending to it.
    //
    // Both, and not gated on side: TerrainOp has no ZNetView, so Awake runs only on the peer that
    // created it, and the record has to be correct wherever that is.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(TerrainOp))]
    internal static class TerrainOpPaintFanoutPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(TerrainOpPaintFanoutPatch),
                ValConfig.SectionTerrain,
                "Fix Terrain Paint Zone Fanout",
                true,
                "Sends terrain paint to every zone the paint actually covers. Vanilla measures which " +
                "zones an edit touches from its radius, but the paint itself reaches about a metre " +
                "further west and south, so the neighbouring zone never records paint applied to ground " +
                "it shares - which is what leaves dirt stopping dead along a 64m zone border.");
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
                Heightmap.FindHeightmap(pos, vanillaRadius, Reached);
                Heightmap.FindHeightmap(pos, paintReach, PaintReached);

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
