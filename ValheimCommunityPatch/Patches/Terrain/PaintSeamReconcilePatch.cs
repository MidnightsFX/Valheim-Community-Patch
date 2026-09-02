using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Fix Terrain Paint Seams: makes the paint on either side of a zone boundary agree, so dirt
    // no longer stops in a hard line along the 64 m grid.
    //
    // Adjacent zones each keep their own copy of the shared boundary texel, in separate
    // TerrainComp data with nothing keeping them in step. TerrainOpPaintFanoutPatch explains why
    // they diverge; this repairs terrain already recorded that way. It has to be more than a
    // per-channel minimum: forcing the shared texel down to the unpainted side turns the last
    // metre into a dirt-to-grass gradient along the line, and the next regeneration knocks it
    // back down again.
    //
    // A postfix on Heightmap.ApplyModifiers reconciles the shared texels in the render texture,
    // per colour channel, from the two boundary texels a and b and the texels sa and sb one step
    // inward on each side:
    //
    //     result = Clamp(Max(sa, sb), Min(a, b), Max(a, b))
    //
    // Paint with interior behind it continues across the line; an isolated one-texel stripe is
    // removed; sides that already agree are unchanged, so regeneration cannot drift. Corners are
    // shared by four zones and get one pass over all four so the value cannot flip between two
    // pairwise answers. Alpha (the cleared-vegetation mask) is world-generated, already
    // consistent, and left alone.
    //
    // Client: only Heightmap.m_paintMask, a render texture that is never saved, is written. The
    // saved paint is unchanged, though a later paint op near the border will seed from the
    // reconciled value. Runtime IsDedicated guard because a server builds these textures too.
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

        // Neighbour textures touched during one pass, uploaded once at the end.
        private static readonly List<Heightmap> DirtyNeighbours = new List<Heightmap>();

        // The up-to-four zones that meet at a corner.
        private static readonly Corner[] CornerBuffer = new Corner[4];

        [HarmonyPostfix]
        [HarmonyPatch("ApplyModifiers")]
        private static void ApplyModifiersPostfix(Heightmap __instance) {
            if (Enabled == null || !Enabled.Value) { return; }

            // IsDedicated rather than IsServer: a listen host draws its own terrain.
            if (RunMode.IsDedicated) { return; }

            if (__instance.IsDistantLod) { return; }
            if (__instance.m_paintMask == null) { return; }

            int width = __instance.m_width;

            // The merge samples one texel inward, so anything narrower has no interior.
            if (width < 2) { return; }

            float zoneSize = width * __instance.m_scale;
            bool changed = false;

            DirtyNeighbours.Clear();

            // All four edges: reconciling is idempotent, and covering every direction means a
            // zone repairs its seams even when only one side ever regenerates.
            changed |= ReconcileEdge(__instance, FindNeighbour(__instance, zoneSize, 0f), Edge.East, width);
            changed |= ReconcileEdge(__instance, FindNeighbour(__instance, -zoneSize, 0f), Edge.West, width);
            changed |= ReconcileEdge(__instance, FindNeighbour(__instance, 0f, zoneSize), Edge.North, width);
            changed |= ReconcileEdge(__instance, FindNeighbour(__instance, 0f, -zoneSize), Edge.South, width);

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

            // 1..width-1: the two ends of every edge are corner texels, handled by ReconcileCorner.
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

                // The common case, and the clamp collapses to this value whatever the inward
                // samples hold, so skip reading them.
                if (SamePaint(a, b)) { continue; }

                Color merged = Merge(
                    a, b,
                    ours.m_paintMask.GetPixel(oix, oiy),
                    theirs.m_paintMask.GetPixel(tix, tiy));

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

        // highX / highZ pick which of this zone's four corners to reconcile. Each participating
        // zone contributes its copy of the corner texel plus the texel diagonally inward from it.
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

        // Alpha is not part of the merge; callers reattach each side's own.
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

        // Zone heightmaps are exactly zoneSize apart, so offsetting by that lands on the
        // neighbour's centre.
        private static Heightmap FindNeighbour(Heightmap hmap, float dx, float dz) {
            Heightmap neighbour = Heightmap.FindHeightmap(hmap.transform.position + new Vector3(dx, 0f, dz));

            if (neighbour == null || neighbour == hmap || neighbour.IsDistantLod) { return null; }
            if (neighbour.m_width != hmap.m_width || neighbour.m_scale != hmap.m_scale) { return null; }

            return neighbour;
        }
    }
}
