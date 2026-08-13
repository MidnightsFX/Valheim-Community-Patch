using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Vanilla defect: terrain paint disagrees between the two sides of a zone boundary, so painted
    // ground (usually dirt, from levelling and raising terrain) stops dead in a hard straight line
    // along the 64 m grid instead of continuing across it.
    //
    // Adjacent zones share their boundary texels - this zone's paint texel (width, i) covers the same
    // ground as the neighbour's (0, i). Both should therefore hold the same colour. Measured with
    // vcp_terrainscan on a live world, 556 of 2600 shared vertices disagreed on the red (dirt)
    // channel, with a maximum delta of 1.0 - fully painted on one side, unpainted on the other -
    // while green, blue and alpha matched exactly.
    //
    // Why it happens, and why there is no clean root-cause fix:
    //
    //   World generation never writes dirt at all. WorldGenerator only ever fills the mask's alpha
    //   (Color.black, or new Color(0,0,0,a) for Mistlands and Ashlands), so every bit of red comes
    //   from a terrain operation. And unlike terrain *height*, which TerrainComp stores as a delta,
    //   paint is stored as an absolute colour snapshot seeded from whatever the heightmap texture
    //   happened to hold at the moment the operation was recorded:
    //
    //     Color a2 = this.m_hmap.GetPaintMask(x2, y2);       // TerrainComp.PaintCleared
    //     ... a2 = Color.Lerp(a2, Heightmap.m_paintMaskDirt, t);
    //     this.m_paintMask[y2 * num4 + x2] = a2;
    //
    //   Every distance and rounding calculation involved is symmetric between the two zones, so the
    //   divergence is not arithmetic - it is state. If the two zones' textures differed at that
    //   instant (different terrain modifiers loaded, different regeneration order), the two terrain
    //   compilers permanently record different absolute colours for the same ground, and because
    //   TerrainComp.ApplyToHeightmap replays that snapshot over the top on every regeneration,
    //   nothing ever reconciles them.
    //
    // Fix: reconcile the shared texels at render time by taking the per-channel *minimum* of the two
    // sides. Minimum rather than maximum or average is deliberate:
    //
    //   * It can only ever remove paint, never invent it, so it cannot create a new artifact.
    //   * Paint applied legitimately across a boundary reaches both zones symmetrically, so both
    //     sides already agree and the minimum is a no-op. Only genuinely divergent - that is, buggy -
    //     texels are affected.
    //   * Both zones compute the same value from the same pair of inputs, so the result is continuous
    //     regardless of which side is processed first.
    //
    // Alpha is deliberately left alone: it is the cleared-vegetation mask, which also drives lava
    // rendering in the Ashlands, it comes from world generation, and it was measured as already
    // consistent across every shared vertex.
    //
    // Writing to both textures means either side can repair the pair, so unlike the normals fix this
    // has no dependency on a zone having all of its neighbours loaded.
    [HarmonyPatch(typeof(Heightmap))]
    internal static class PaintSeamReconcilePatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindServerConfig(
                ValConfig.SectionTerrain,
                "Fix Terrain Paint Seams",
                true,
                "Makes terrain paint agree along zone boundaries. Vanilla can end up with dirt painted on " +
                "only one side of a 64m zone border, which draws a hard straight line across the ground. " +
                "Only ever removes paint from the boundary itself, never adds it, so terrain painted " +
                "normally across a border is unaffected.");
        }

        [HarmonyPostfix]
        [HarmonyPatch("ApplyModifiers")]
        private static void ApplyModifiersPostfix(Heightmap __instance) {
            if (Enabled == null || !Enabled.Value) { return; }
            if (__instance.IsDistantLod) { return; }
            if (__instance.m_paintMask == null) { return; }

            float zoneSize = __instance.m_width * __instance.m_scale;
            int width = __instance.m_width;
            bool changed = false;

            // All four edges rather than just two: reconciling is idempotent, and covering every
            // direction means a zone repairs its seams even when only one side ever regenerates.
            changed |= ReconcileEdge(__instance, FindNeighbour(__instance, zoneSize, 0f), Edge.East, width);
            changed |= ReconcileEdge(__instance, FindNeighbour(__instance, -zoneSize, 0f), Edge.West, width);
            changed |= ReconcileEdge(__instance, FindNeighbour(__instance, 0f, zoneSize), Edge.North, width);
            changed |= ReconcileEdge(__instance, FindNeighbour(__instance, 0f, -zoneSize), Edge.South, width);

            if (changed) { __instance.m_paintMask.Apply(); }
        }

        private enum Edge { East, West, North, South }

        private static bool ReconcileEdge(Heightmap ours, Heightmap theirs, Edge edge, int width) {
            if (theirs == null || theirs.m_paintMask == null) { return false; }

            bool changedOurs = false, changedTheirs = false;

            for (int i = 0; i <= width; i++) {
                int ox, oy, tx, ty;
                switch (edge) {
                    case Edge.East: ox = width; oy = i; tx = 0; ty = i; break;
                    case Edge.West: ox = 0; oy = i; tx = width; ty = i; break;
                    case Edge.North: ox = i; oy = width; tx = i; ty = 0; break;
                    default: ox = i; oy = 0; tx = i; ty = width; break;
                }

                Color a = ours.m_paintMask.GetPixel(ox, oy);
                Color b = theirs.m_paintMask.GetPixel(tx, ty);

                // Alpha is world-generated and already consistent; only the paint channels reconcile.
                Color merged = new Color(
                    Mathf.Min(a.r, b.r), Mathf.Min(a.g, b.g), Mathf.Min(a.b, b.b), a.a);

                if (merged.r != a.r || merged.g != a.g || merged.b != a.b) {
                    ours.m_paintMask.SetPixel(ox, oy, merged);
                    changedOurs = true;
                }

                if (merged.r != b.r || merged.g != b.g || merged.b != b.b) {
                    theirs.m_paintMask.SetPixel(tx, ty, new Color(merged.r, merged.g, merged.b, b.a));
                    changedTheirs = true;
                }
            }

            // Their texture is not the one our caller is about to upload, so it needs its own Apply.
            if (changedTheirs) { theirs.m_paintMask.Apply(); }

            return changedOurs;
        }

        // Zone heightmaps are exactly zoneSize apart, so offsetting by that lands on the neighbour's
        // centre - unambiguous even though IsPointInside treats its bounds as inclusive on both edges.
        private static Heightmap FindNeighbour(Heightmap hmap, float dx, float dz) {
            Heightmap neighbour = Heightmap.FindHeightmap(hmap.transform.position + new Vector3(dx, 0f, dz));

            if (neighbour == null || neighbour == hmap || neighbour.IsDistantLod) { return null; }
            if (neighbour.m_width != hmap.m_width || neighbour.m_scale != hmap.m_scale) { return null; }

            return neighbour;
        }
    }
}
