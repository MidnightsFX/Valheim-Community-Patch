using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Fix Terrain Height Seams: the ground on both sides of a zone border meets at one height, closing
    // cracks along the 64 m grid through which the sky or water below shows.
    //
    // Adjacent zones each keep their own copy of the vertices on their shared border, in separate
    // TerrainComp data, and each zone's heightmap builds its border from its own copy. The copies stay
    // equal only while every edit reaches both compilers. Once one side has missed edits (its compiler
    // was destroyed and started over, as Fix Terrain Compiler Duplicates describes, or an edit reached
    // one side only), the border is drawn and collides at two heights and the ground cracks along the
    // line. Later edits do not close it: each side moves its own copy, and the cultivator's smoothing
    // stops at its 1 m limit.
    //
    // A postfix on Heightmap.ApplyModifiers, where a rebuild has just computed the zone's heights,
    // records its border as built and sets every border vertex to the mean of the recorded borders of
    // the loaded zones that share it (two along an edge, up to four at a corner). Vertices whose copies
    // already agree keep their built value bit for bit. The mean is summed in one world order and
    // counts every loaded neighbour, queued to rebuild or not, so every zone sharing a vertex arrives at
    // the same bits: a mean that depended on which neighbours were queued at the moment differed from
    // zone to zone, each poked the other, and in heavily edited towns the zones rebuilt each other
    // hundreds of times a minute. A neighbour that draws another value is poked to rebuild unless it is
    // already queued for a full one. When this zone's own border changed on an edge (an edit, or its
    // compiler loading) and the neighbour across it is queued, that neighbour is rebuilt at once, so its
    // record is current before the mean is taken and the edit does not flash the seam for a frame. A zone
    // whose saved compiler has not spawned yet is built from the unedited ground and is left out until the
    // compiler lands and rebuilds it; counting that first build rebuilt every neighbour twice per zone load.
    // TerrainComp.InternalDoOperation runs with the zone's built border put back, so every edit reads
    // and saves exactly what vanilla would: the saved terrain is never changed by this fix, and turning
    // it off shows the ground as recorded.
    //
    // Both: the heights are also the collider, on every peer that builds the zone.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class HeightSeamReconcilePatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(HeightSeamReconcilePatch),
                ValConfig.SectionTerrain,
                "Fix Terrain Height Seams",
                true,
                "Makes the ground on both sides of a zone border meet at the same height. Each zone keeps " +
                "its own copy of the border, and once one side has missed some edits the two copies differ: " +
                "the ground cracks along the 64m zone line and the sky or water shows through. The border is " +
                "drawn and collides at the average of the two; the saved terrain is not changed.");

            // Rebuild what is loaded so the change shows at once; each rebuild records or drops the mean.
            Enabled.SettingChanged += (sender, args) => {
                foreach (Heightmap hmap in Heightmap.GetAllHeightmaps()) {
                    if (hmap != null && !hmap.IsDistantLod) { hmap.Poke(1); }
                }
            };
        }

        // A border vertex off the mean by more than this has its zone rebuilt.
        private const float RebuildTolerance = 0.001f;

        // Each zone heightmap's border heights as its last rebuild computed them, per border slot. Keyed by
        // instance id, removed in OnDestroy (see TeardownHooks for why an int key).
        private static readonly Dictionary<int, float[]> Built = new Dictionary<int, float[]>();

        // Border slot tables for one heightmap width (zone heightmaps all share it).
        private static int s_tableWidth = -1;
        private static int[] s_slotOfVertex;
        private static int[] s_vertexOfSlot;

        // The loaded neighbours with a recorded border of the zone being reconciled, indexed (dz + 1) * 3 + (dx + 1),
        // their borders, and the ones to poke: one set per pass level, since a pass can rebuild a neighbour from
        // inside itself (RebuildQueuedAcrossSeam).
        private static readonly Heightmap[][] NeighbourAt = { new Heightmap[9], new Heightmap[9] };
        private static readonly float[][][] BuiltAt = { new float[9][], new float[9][] };
        private static readonly List<Heightmap>[] ToRebuildAt = { new List<Heightmap>(), new List<Heightmap>() };
        private static int s_level;

        // The border the zone being reconciled had built before this rebuild.
        private static float[] s_previous = new float[0];

        // Zones whose pass is running. Each sets its own border to the mean, so a pass inside another never pokes them.
        private static readonly List<Heightmap> InPass = new List<Heightmap>(2);

        [HarmonyPostfix]
        // Last, so the border recorded as built includes what other mods' postfixes change.
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(Heightmap), "ApplyModifiers")]
        private static void ApplyModifiersPostfix(Heightmap __instance) {
            if (Enabled == null || !Enabled.Value) { return; }
            if (__instance.IsDistantLod || s_level >= NeighbourAt.Length) { return; }

            List<float> heights = __instance.m_heights;
            int width = __instance.m_width;
            int pitch = width + 1;
            if (width < 1 || heights == null || heights.Count != pitch * pitch) { return; }

            EnsureTables(width);
            int slots = s_vertexOfSlot.Length;

            int id = __instance.GetInstanceID();
            // Unedited ground until its saved compiler spawns.
            if (TerrainComp.FindTerrainCompiler(__instance.transform.position) == null &&
                TerrainCompDuplicatePatch.FindSavedCompilers(__instance, null)) {
                Built.Remove(id);
                return;
            }

            bool hadBorder = Built.TryGetValue(id, out float[] built) && built.Length == slots;
            if (!hadBorder) {
                built = new float[slots];
                Built[id] = built;
            } else if (s_level == 0) {
                if (s_previous.Length != slots) { s_previous = new float[slots]; }
                Array.Copy(built, s_previous, slots);
            }
            for (int s = 0; s < slots; s++) { built[s] = heights[s_vertexOfSlot[s]]; }

            Heightmap[] neighbours = NeighbourAt[s_level];
            float[][] borders = BuiltAt[s_level];
            List<Heightmap> toRebuild = ToRebuildAt[s_level];
            InPass.Add(__instance);
            try {
                if (FindNeighbours(__instance, neighbours, borders)) {
                    if (hadBorder && s_level == 0) { RebuildQueuedAcrossSeam(__instance, built, neighbours); }
                    Reconcile(__instance, built, neighbours, borders, toRebuild);
                }
            } finally {
                RemoveRef(InPass, __instance);
                Array.Clear(neighbours, 0, neighbours.Length);
                Array.Clear(borders, 0, borders.Length);
                toRebuild.Clear();
            }
        }

        // Unity's == on these lists' elements is a native call; a pass only needs identity.
        private static bool HasRef(List<Heightmap> list, Heightmap h) {
            for (int i = 0; i < list.Count; i++) {
                if (ReferenceEquals(list[i], h)) { return true; }
            }
            return false;
        }

        private static void RemoveRef(List<Heightmap> list, Heightmap h) {
            for (int i = list.Count - 1; i >= 0; i--) {
                if (ReferenceEquals(list[i], h)) {
                    list.RemoveAt(i);
                    return;
                }
            }
        }

        // Sets this zone's shared border vertices to the mean, and pokes each neighbour that draws another value.
        private static void Reconcile(Heightmap hmap, float[] built, Heightmap[] neighbours, float[][] borders, List<Heightmap> toRebuild) {
            List<float> heights = hmap.m_heights;
            int width = hmap.m_width;
            int pitch = width + 1;
            int slots = s_vertexOfSlot.Length;

            toRebuild.Clear();
            for (int s = 0; s < slots; s++) {
                int v = s_vertexOfSlot[s];
                int x = v % pitch, y = v / pitch;
                float own = built[s];
                float sum = 0f, min = own, max = own;
                int count = 0;

                // The zones that share this vertex, this one included, south to north and west to east: every one
                // of them adds the same values in the same order, so float rounding cannot set them apart.
                for (int dz = y == 0 ? -1 : 0; dz <= (y == width ? 1 : 0); dz++) {
                    for (int dx = x == 0 ? -1 : 0; dx <= (x == width ? 1 : 0); dx++) {
                        float h = own;
                        if (dx != 0 || dz != 0) {
                            int k = (dz + 1) * 3 + dx + 1;
                            if (ReferenceEquals(neighbours[k], null)) { continue; }
                            h = borders[k][s_slotOfVertex[(y - dz * width) * pitch + x - dx * width]];
                        }
                        sum += h;
                        count++;
                        if (h < min) { min = h; }
                        if (h > max) { max = h; }
                    }
                }

                if (count == 1) { continue; }

                // Copies that agree keep the built value exactly; the neighbours may still draw an older mean.
                float mean = min == max ? own : sum / count;
                heights[v] = mean;

                for (int dz = y == 0 ? -1 : 0; dz <= (y == width ? 1 : 0); dz++) {
                    for (int dx = x == 0 ? -1 : 0; dx <= (x == width ? 1 : 0); dx++) {
                        if (dx == 0 && dz == 0) { continue; }
                        Heightmap other = neighbours[(dz + 1) * 3 + dx + 1];
                        if (ReferenceEquals(other, null) || HasRef(toRebuild, other) || HasRef(InPass, other)) { continue; }
                        // Its own pass takes this same mean. A paint-only request keeps the meshes, so that one is poked
                        // to make it full.
                        if (other.m_doLateUpdate != 0 && other.m_regenRequest == Heightmap.RegenRequest.Full) { continue; }
                        float drawn = other.m_heights[(y - dz * width) * pitch + x - dx * width];
                        if (Mathf.Abs(drawn - mean) > RebuildTolerance) { toRebuild.Add(other); }
                    }
                }
            }

            // Delayed like a terrain edit's own rebuild, never nested inside this one.
            for (int i = 0; i < toRebuild.Count; i++) { toRebuild[i].Poke(1); }
        }

        // Rebuilds now each queued neighbour across an edge where this zone's own border just changed, so its record
        // is current when the mean is taken and both draw the new mean this frame. Without it an edit across a seam
        // drew a mean of the new and the old heights for a frame. Regenerate clears the queued rebuild, so it does
        // not run twice.
        private static void RebuildQueuedAcrossSeam(Heightmap hmap, float[] built, Heightmap[] neighbours) {
            int width = hmap.m_width;
            int pitch = width + 1;

            for (int dz = -1; dz <= 1; dz++) {
                for (int dx = -1; dx <= 1; dx++) {
                    if (dx == 0 && dz == 0) { continue; }
                    Heightmap other = neighbours[(dz + 1) * 3 + dx + 1];
                    if (ReferenceEquals(other, null) || other.m_doLateUpdate == 0 || HasRef(InPass, other)) { continue; }

                    // The shared vertices: a row or column along an edge, the one corner vertex diagonally.
                    bool changed = false;
                    int count = dx != 0 && dz != 0 ? 1 : pitch;
                    for (int i = 0; i < count && !changed; i++) {
                        int x = dx > 0 ? width : dx < 0 ? 0 : i;
                        int y = dz > 0 ? width : dz < 0 ? 0 : i;
                        int slot = s_slotOfVertex[y * pitch + x];
                        changed = built[slot] != s_previous[slot];
                    }
                    if (!changed) { continue; }

                    // A full rebuild now, as vanilla's own Poke does it; a paint-only request is made full by it.
                    s_level++;
                    try {
                        other.Poke();
                    } finally {
                        s_level--;
                    }
                }
            }
        }

        // An edit reads the zone's heights as built, so what it saves is vanilla's.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TerrainComp), "InternalDoOperation")]
        private static void InternalDoOperationPrefix(TerrainComp __instance, out Swap __state) {
            __state = null;
            if (Enabled == null || !Enabled.Value) { return; }

            Heightmap hmap = __instance.m_hmap;
            if (hmap == null || !Built.TryGetValue(hmap.GetInstanceID(), out float[] built)) { return; }

            List<float> heights = hmap.m_heights;
            int pitch = hmap.m_width + 1;
            if (hmap.m_width != s_tableWidth || heights == null || heights.Count != pitch * pitch) { return; }

            for (int s = 0; s < built.Length; s++) {
                int v = s_vertexOfSlot[s];
                if (heights[v] == built[s]) { continue; }
                if (__state == null) { __state = new Swap { Map = hmap, Drawn = new float[built.Length] }; }
                __state.Drawn[s] = heights[v];
                __state.Moved.Add(s);
                heights[v] = built[s];
            }
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(TerrainComp), "InternalDoOperation")]
        private static void InternalDoOperationFinalizer(Swap __state) {
            if (__state == null || __state.Map == null) { return; }

            List<float> heights = __state.Map.m_heights;
            foreach (int s in __state.Moved) { heights[s_vertexOfSlot[s]] = __state.Drawn[s]; }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Heightmap), "OnDestroy")]
        private static void OnDestroyPostfix(Heightmap __instance) => Built.Remove(__instance.GetInstanceID());

        private sealed class Swap {
            internal Heightmap Map;
            internal float[] Drawn;
            internal readonly List<int> Moved = new List<int>();
        }

        // Fills neighbours (and their recorded borders) with the loaded zone heightmaps touching this one that have
        // a recorded border. False when there are none.
        private static bool FindNeighbours(Heightmap hmap, Heightmap[] neighbours, float[][] borders) {
            float size = hmap.m_width * hmap.m_scale;
            Vector3 centre = hmap.transform.position;
            bool any = false;
            for (int dz = -1; dz <= 1; dz++) {
                for (int dx = -1; dx <= 1; dx++) {
                    if (dx == 0 && dz == 0) { continue; }
                    Heightmap other = Neighbour(hmap, centre, size, dx, dz, out float[] theirs);
                    if (ReferenceEquals(other, null)) { continue; }
                    int k = (dz + 1) * 3 + dx + 1;
                    neighbours[k] = other;
                    borders[k] = theirs;
                    any = true;
                }
            }
            return any;
        }

        // The loaded zone heightmap of the same size at this offset in zones, when it has a recorded border.
        private static Heightmap Neighbour(Heightmap hmap, Vector3 centre, float size, int dx, int dz, out float[] theirs) {
            theirs = null;
            Heightmap other = Heightmap.FindHeightmap(centre + new Vector3(dx * size, 0f, dz * size));
            if (other == null || ReferenceEquals(other, hmap) || other.IsDistantLod) { return null; }
            if (other.m_width != hmap.m_width || other.m_scale != hmap.m_scale) { return null; }

            // Zone heightmaps sit on the grid; anything else is not a neighbour that shares vertices.
            Vector3 offset = other.transform.position - centre;
            if (Mathf.Abs(offset.x - dx * size) > 0.01f || Mathf.Abs(offset.z - dz * size) > 0.01f) { return null; }

            if (!Built.TryGetValue(other.GetInstanceID(), out theirs) || theirs.Length != s_vertexOfSlot.Length) {
                theirs = null;
                return null;
            }
            if (other.m_heights == null || other.m_heights.Count != hmap.m_heights.Count) {
                theirs = null;
                return null;
            }
            return other;
        }

        private static void EnsureTables(int width) {
            if (width == s_tableWidth) { return; }

            int pitch = width + 1;
            s_slotOfVertex = new int[pitch * pitch];
            List<int> border = new List<int>(4 * width);
            for (int y = 0; y < pitch; y++) {
                for (int x = 0; x < pitch; x++) {
                    int v = y * pitch + x;
                    if (x == 0 || y == 0 || x == width || y == width) {
                        s_slotOfVertex[v] = border.Count;
                        border.Add(v);
                    } else {
                        s_slotOfVertex[v] = -1;
                    }
                }
            }
            s_vertexOfSlot = border.ToArray();

            // Borders recorded at another width index differently.
            Built.Clear();
            s_tableWidth = width;
        }
    }
}
