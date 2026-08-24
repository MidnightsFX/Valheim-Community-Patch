using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Vanilla defect: the terrain paint kernel reaches about a metre further than the radius the
    // operation is fanned out with, so zones just past that metre never record paint that was applied
    // to the ground they own.
    //
    //   TerrainOp.Awake decides which zones an operation touches from its radius alone:
    //
    //     Heightmap.FindHeightmap(this.transform.position, this.GetRadius(), heightmaps);
    //     foreach (Heightmap heightmap in heightmaps)
    //       heightmap.GetAndCreateTerrainCompiler().ApplyOperation(this);
    //
    //   but TerrainComp.PaintCleared shifts the position half a texel and then floors it:
    //
    //     worldPos.x -= 0.5f;  worldPos.z -= 0.5f;
    //     this.m_hmap.WorldToVertexMask(worldPos, out x1, out y1);   // FloorToInt inside
    //
    //   The two halves cancel inside WorldToVertexMask, leaving a plain floor, so the kernel's centre
    //   lands on the vertex grid up to a full texel toward -x/-z of where the player actually
    //   painted. Everything downstream is measured from that shifted centre, which pushes the
    //   kernel's -x/-z reach past GetRadius() by the same amount.
    //
    //   Adjacent zones each hold their own copy of the shared boundary texel, so an operation placed
    //   in that band paints its own zone's column 0 / row 0 - the shared texel - while the west or
    //   south neighbour, excluded from the fan-out, keeps the unpainted copy. The two sides then
    //   disagree about the same patch of ground for good: TerrainComp stores paint as an absolute
    //   colour snapshot and replays it over the top on every regeneration, so nothing reconciles them
    //   afterwards.
    //
    // Only paint is affected. LevelTerrain, RaiseTerrain and SmoothTerrain all call WorldToVertex
    // without the half-texel shift, which rounds to nearest and stays symmetric about the operation.
    //
    // Fix: after vanilla has fanned the operation out, work out which extra zones the paint kernel
    // genuinely reaches and send those a paint-only copy of the same operation, so both sides of the
    // boundary record it and the agreement is saved rather than patched up at render time.
    //
    //   * Paint-only, because level/raise/smooth do not over-reach - handing them to a zone vanilla
    //     deliberately left out would move terrain nothing asked to move. The settings object is
    //     temporarily masked around the call; ApplyOperation serialises it into its ZPackage
    //     synchronously, and the operation is destroyed at the end of this frame, so nothing else can
    //     observe the change.
    //   * Only zones on the -x/-z side of the operation are considered, because that is the only
    //     direction the floor can run past the radius. A zone to the east or north is already inside
    //     vanilla's fan-out whenever the kernel touches it.
    //   * Running as a postfix rather than replacing Awake keeps vanilla's own fan-out and OnPlaced
    //     intact. Destroy(gameObject) at the end of Awake is deferred to the end of the frame, so the
    //     operation's transform and settings are still live here.
    //
    // This only helps operations placed from now on. PaintSeamReconcilePatch is what repairs
    // boundaries in terrain that was already recorded the broken way.
    //
    // Both, and deliberately ungated: this changes what is *recorded*, not what is drawn, so it has
    // to run wherever the operation is created. TerrainOp has no ZNetView, so Awake only fires on
    // the peer that instantiated it - a player placing or attacking, but also a server-owned
    // creature attacking inside the server's own active area.
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
            // Harmony still runs postfixes when another mod's prefix skipped the original, and there
            // is no fan-out to extend if vanilla never ran one.
            if (!__runOriginal) { return; }

            if (Enabled == null || !Enabled.Value) { return; }

            // Vanilla returned before touching anything, so there is no fan-out to extend.
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

        // True when the zone lies on the -x or -z side of the operation, which is the only direction
        // the floored kernel centre can carry paint past the fan-out radius.
        private static bool IsBehindOperation(Heightmap hmap, Vector3 pos) {
            float half = hmap.m_width * hmap.m_scale * 0.5f;
            Vector3 centre = hmap.transform.position;

            return centre.x + half <= pos.x || centre.z + half <= pos.z;
        }
    }
}
