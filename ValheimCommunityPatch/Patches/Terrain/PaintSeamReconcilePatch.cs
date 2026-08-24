using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Vanilla defect: terrain paint disagrees between the two sides of a zone boundary, so painted
    // ground (usually dirt, from levelling and raising terrain) stops dead in a hard straight line
    // along the 64 m grid instead of continuing across it.
    //
    // Adjacent zones each keep their own *copy* of the shared boundary texel - this zone's paint
    // texel (width, i) covers the same ground as the neighbour's (0, i), stored twice in two separate
    // TerrainComp ZDOs with nothing keeping them in step. Measured on a live world, 556 of 2600
    // shared vertices disagreed on the red (dirt) channel, with a maximum delta of 1.0 - fully
    // painted on one side, unpainted on the other - while green, blue and alpha matched exactly.
    //
    // Why they diverge:
    //
    //   TerrainOp.Awake fans an operation out to every zone it can reach, using the operation's own
    //   radius:
    //
    //     Heightmap.FindHeightmap(this.transform.position, this.GetRadius(), heightmaps);
    //
    //   but TerrainComp.PaintCleared shifts the position half a texel and *then* floors it:
    //
    //     worldPos.x -= 0.5f;  worldPos.z -= 0.5f;
    //     this.m_hmap.WorldToVertexMask(worldPos, out x1, out y1);   // FloorToInt inside
    //
    //   so the paint kernel's centre snaps to the vertex grid up to a full texel toward -x/-z, and
    //   the kernel therefore reaches about a metre further than GetRadius() on those two sides. An
    //   operation placed near a zone's west or south border paints that zone's own column 0 / row 0 -
    //   which *is* the shared boundary texel - while the west or south neighbour was never included
    //   in the fan-out and so never records it. The asymmetry is directional, which is why the
    //   artifact only ever shows along one side of a chunk.
    //
    //   Note this is specific to paint. LevelTerrain, RaiseTerrain and SmoothTerrain all call
    //   WorldToVertex without the half-texel shift, which rounds to nearest and stays symmetric.
    //   TerrainOpPaintFanoutPatch closes the gap for new operations; this patch repairs the boundary
    //   for terrain that was already recorded that way.
    //
    // Why the seam is visible at all, and why a naive fix makes it worse:
    //
    //   RebuildRenderMesh assigns UV (x / m_width, y / m_width), so a zone's last one-metre row of
    //   quads interpolates between texel width-1 and texel width. Forcing the shared texel down to
    //   the unpainted neighbour's value - which is what an unconditional per-channel minimum does -
    //   turns that last metre into a dirt-to-grass gradient, a pale band running along the grid line,
    //   and makes the border column impossible to paint: Heightmap.Generate rebuilds the mask from
    //   the base mask plus TerrainComp on every regeneration, and the reconcile then knocks the
    //   border back down again every single time.
    //
    // Fix: reconcile the shared texels at render time to a value the surrounding terrain actually
    // supports. Per channel, with `a`/`b` the two sides' boundary texels and `sa`/`sb` the texel one
    // step inward on each side:
    //
    //     result = Clamp(Max(sa, sb), Min(a, b), Max(a, b))
    //
    //   * Paint that legitimately reaches the border has the painted side's own interior behind it,
    //     so the maximum wins and the paint continues about a metre into the neighbour rather than
    //     fading out short of the line.
    //   * A genuinely isolated one-texel stripe has nothing behind it on either side, so the minimum
    //     wins and the stripe is still removed.
    //   * When the two sides already agree, lo == hi and the result is that value regardless of the
    //     support term, so repeated regeneration cannot drift.
    //   * Both zones compute the same value from the same four inputs, so the result is continuous
    //     regardless of which side is processed first.
    //
    // Unlike the previous minimum-only rule this can add paint as well as remove it, up to one texel
    // past where the player painted. That is the correct outcome given the shared-texel layout: the
    // ground at the border genuinely is painted, and there is no way to represent paint stopping
    // exactly on the line without a discontinuity.
    //
    // Alpha is deliberately left alone: it is the cleared-vegetation mask, which also drives lava
    // rendering in the Ashlands, it comes from world generation, and it was measured as already
    // consistent across every shared vertex.
    //
    // Writing to every texture involved means any side can repair the set, so unlike the normals fix
    // this has no dependency on a zone having all of its neighbours loaded.
    //
    // Client: Heightmap.m_paintMask is a Texture2D bound to the terrain material and is never
    // serialised - the saved paint is TerrainComp's Color[] - so this only ever changes what is
    // drawn. Like SeamlessNormalsPatch the target does run on a dedicated server, so the runtime
    // IsDedicated guard stays alongside the patch-time gate.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(Heightmap))]
    internal static class PaintSeamReconcilePatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(PaintSeamReconcilePatch),
                ValConfig.SectionTerrain,
                "Fix Terrain Paint Seams",
                true,
                "Makes terrain paint agree along zone boundaries. Vanilla can end up with dirt painted on " +
                "only one side of a 64m zone border, which draws a hard straight line across the ground. " +
                "Paint that the ground next to the border already carries is carried across the boundary; " +
                "a lone stripe sitting on the border with nothing behind it on either side is removed.");
        }

        // Neighbouring textures touched during one pass, uploaded once at the end rather than once per
        // edge and corner.
        private static readonly List<Heightmap> DirtyNeighbours = new List<Heightmap>();

        // The four zones that meet at a corner, read once each.
        private static readonly Corner[] CornerBuffer = new Corner[4];

        [HarmonyPostfix]
        [HarmonyPatch("ApplyModifiers")]
        private static void ApplyModifiersPostfix(Heightmap __instance) {
            if (Enabled == null || !Enabled.Value) { return; }

            // A dedicated server maintains these paint textures but never renders them, so reconciling
            // its boundaries is pure cost. IsDedicated rather than IsServer: a listen host draws its
            // own terrain and needs the fix.
            if (RunMode.IsDedicated) { return; }

            if (__instance.IsDistantLod) { return; }
            if (__instance.m_paintMask == null) { return; }

            int width = __instance.m_width;

            // The merge samples one texel inward, so anything narrower than this has no interior to
            // sample and there is nothing sensible to do.
            if (width < 2) { return; }

            float zoneSize = width * __instance.m_scale;
            bool changed = false;

            DirtyNeighbours.Clear();

            // All four edges rather than just two: reconciling is idempotent, and covering every
            // direction means a zone repairs its seams even when only one side ever regenerates.
            changed |= ReconcileEdge(__instance, FindNeighbour(__instance, zoneSize, 0f), Edge.East, width);
            changed |= ReconcileEdge(__instance, FindNeighbour(__instance, -zoneSize, 0f), Edge.West, width);
            changed |= ReconcileEdge(__instance, FindNeighbour(__instance, 0f, zoneSize), Edge.North, width);
            changed |= ReconcileEdge(__instance, FindNeighbour(__instance, 0f, -zoneSize), Edge.South, width);

            // Corners are shared by four zones, not two. Reconciling one pairwise from the east edge
            // and then again from the north edge would settle on two different values and flip between
            // them on successive regenerations, so they get a single pass over the whole set instead.
            changed |= ReconcileCorner(__instance, zoneSize, true, true, width);
            changed |= ReconcileCorner(__instance, zoneSize, true, false, width);
            changed |= ReconcileCorner(__instance, zoneSize, false, true, width);
            changed |= ReconcileCorner(__instance, zoneSize, false, false, width);

            if (changed) { __instance.m_paintMask.Apply(); }

            for (int i = 0; i < DirtyNeighbours.Count; i++) {
                DirtyNeighbours[i].m_paintMask.Apply();
            }

            DirtyNeighbours.Clear();
        }

        private enum Edge { East, West, North, South }

        private struct Corner {
            internal Heightmap Map;
            internal int X;
            internal int Y;
            internal Color Boundary;
            internal Color Inward;
        }

        private static bool ReconcileEdge(Heightmap ours, Heightmap theirs, Edge edge, int width) {
            if (theirs == null || theirs.m_paintMask == null) { return false; }

            bool changedOurs = false, changedTheirs = false;

            // 1..width-1, not 0..width: the two ends of every edge are corner texels, handled once
            // over all four zones rather than twice pairwise.
            for (int i = 1; i < width; i++) {
                int ox, oy, oix, oiy, tx, ty, tix, tiy;
                switch (edge) {
                    case Edge.East:
                        ox = width; oy = i; oix = width - 1; oiy = i;
                        tx = 0; ty = i; tix = 1; tiy = i;
                        break;
                    case Edge.West:
                        ox = 0; oy = i; oix = 1; oiy = i;
                        tx = width; ty = i; tix = width - 1; tiy = i;
                        break;
                    case Edge.North:
                        ox = i; oy = width; oix = i; oiy = width - 1;
                        tx = i; ty = 0; tix = i; tiy = 1;
                        break;
                    default:
                        ox = i; oy = 0; oix = i; oiy = 1;
                        tx = i; ty = width; tix = i; tiy = width - 1;
                        break;
                }

                Color a = ours.m_paintMask.GetPixel(ox, oy);
                Color b = theirs.m_paintMask.GetPixel(tx, ty);

                // By far the common case, and the clamp collapses to that value whatever the support
                // samples hold - so skip reading them.
                if (SamePaint(a, b)) { continue; }

                Color merged = Merge(
                    a, b,
                    ours.m_paintMask.GetPixel(oix, oiy),
                    theirs.m_paintMask.GetPixel(tix, tiy));

                // Alpha is world-generated and already consistent; each side keeps its own.
                if (!SamePaint(merged, a)) {
                    ours.m_paintMask.SetPixel(ox, oy, new Color(merged.r, merged.g, merged.b, a.a));
                    changedOurs = true;
                }

                if (!SamePaint(merged, b)) {
                    theirs.m_paintMask.SetPixel(tx, ty, new Color(merged.r, merged.g, merged.b, b.a));
                    changedTheirs = true;
                }
            }

            if (changedTheirs) { MarkDirty(theirs); }

            return changedOurs;
        }

        // `highX` / `highZ` pick which of this zone's four corners to reconcile: true means the +x or
        // +z side. Each participating zone contributes its own copy of that corner texel plus the
        // texel diagonally inward from it, which is the only sample at a corner that is not itself on
        // a boundary.
        private static bool ReconcileCorner(Heightmap ours, float zoneSize, bool highX, bool highZ, int width) {
            float dx = highX ? zoneSize : -zoneSize;
            float dz = highZ ? zoneSize : -zoneSize;

            int count = 0;
            count = AddCorner(count, ours, width, highX, highZ);
            count = AddCorner(count, FindNeighbour(ours, dx, 0f), width, !highX, highZ);
            count = AddCorner(count, FindNeighbour(ours, 0f, dz), width, highX, !highZ);
            count = AddCorner(count, FindNeighbour(ours, dx, dz), width, !highX, !highZ);

            if (count < 2) { return false; }

            Color lo = CornerBuffer[0].Boundary;
            Color hi = CornerBuffer[0].Boundary;
            Color sup = CornerBuffer[0].Inward;

            for (int i = 1; i < count; i++) {
                Color e = CornerBuffer[i].Boundary;
                Color s = CornerBuffer[i].Inward;

                lo = new Color(Mathf.Min(lo.r, e.r), Mathf.Min(lo.g, e.g), Mathf.Min(lo.b, e.b));
                hi = new Color(Mathf.Max(hi.r, e.r), Mathf.Max(hi.g, e.g), Mathf.Max(hi.b, e.b));
                sup = new Color(Mathf.Max(sup.r, s.r), Mathf.Max(sup.g, s.g), Mathf.Max(sup.b, s.b));
            }

            Color merged = new Color(
                Mathf.Clamp(sup.r, lo.r, hi.r),
                Mathf.Clamp(sup.g, lo.g, hi.g),
                Mathf.Clamp(sup.b, lo.b, hi.b));

            bool changedOurs = false;

            for (int i = 0; i < count; i++) {
                Corner corner = CornerBuffer[i];
                if (SamePaint(merged, corner.Boundary)) { continue; }

                corner.Map.m_paintMask.SetPixel(
                    corner.X, corner.Y, new Color(merged.r, merged.g, merged.b, corner.Boundary.a));

                if (corner.Map == ours) { changedOurs = true; } else { MarkDirty(corner.Map); }
            }

            return changedOurs;
        }

        private static int AddCorner(int count, Heightmap hmap, int width, bool highX, bool highZ) {
            if (hmap == null || hmap.m_paintMask == null) { return count; }

            int x = highX ? width : 0;
            int y = highZ ? width : 0;
            int inwardX = highX ? width - 1 : 1;
            int inwardY = highZ ? width - 1 : 1;

            CornerBuffer[count] = new Corner {
                Map = hmap,
                X = x,
                Y = y,
                Boundary = hmap.m_paintMask.GetPixel(x, y),
                Inward = hmap.m_paintMask.GetPixel(inwardX, inwardY),
            };

            return count + 1;
        }

        // Alpha is not part of the merge, so the returned colour carries none - callers reattach each
        // side's own.
        private static Color Merge(Color a, Color b, Color supportA, Color supportB) => new Color(
            MergeChannel(a.r, b.r, supportA.r, supportB.r),
            MergeChannel(a.g, b.g, supportA.g, supportB.g),
            MergeChannel(a.b, b.b, supportA.b, supportB.b));

        private static float MergeChannel(float a, float b, float supportA, float supportB) =>
            Mathf.Clamp(Mathf.Max(supportA, supportB), Mathf.Min(a, b), Mathf.Max(a, b));

        private static bool SamePaint(Color merged, Color current) =>
            merged.r == current.r && merged.g == current.g && merged.b == current.b;

        private static void MarkDirty(Heightmap hmap) {
            if (!DirtyNeighbours.Contains(hmap)) { DirtyNeighbours.Add(hmap); }
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
