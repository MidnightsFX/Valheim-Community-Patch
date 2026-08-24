using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Vanilla defect: Heightmap.RebuildRenderMesh computes vertex normals with
    // m_renderMesh.RecalculateNormals(). Each zone's mesh contains only its own 65x65 vertices, so a
    // vertex sitting on a zone boundary gets its normal averaged from *only the triangles on that
    // zone's side*. The neighbouring zone owns a vertex at the identical world position and averages
    // it from the other side, producing a different normal.
    //
    // The result is a hard lighting crease along every 64 m zone boundary. It is most visible on flat,
    // brightly lit terrain - Plains and Meadows - which is where players report seeing seams.
    //
    // Note the base heightfield itself is *not* discontinuous: HeightmapBuilder.Build blends the four
    // corner-biome heights with SmoothStep-warped coordinates, and because SmoothStep(0,1,0)==0 and
    // SmoothStep(0,1,1)==1 while adjacent zones sample WorldGenerator.GetBiome at the same world
    // positions for their shared corners, both zones compute an identical height at every shared
    // vertex. The geometry lines up; only the shading does not.
    //
    // Fix: replace the mesh normals with analytic ones derived from the height field by central
    // difference, taking out-of-range samples from the neighbouring heightmap instead of vanilla's
    // GetHeight returning 0 past the edge. That makes the normal at a shared vertex a function of the
    // surrounding terrain rather than of which zone happens to own it, so both sides agree.
    //
    // Client: normals are shading only, and collision does not use them. This is one of the two
    // fixes whose target method genuinely runs and does real work on a dedicated server - the render
    // mesh is built there for the MeshCollider - so it keeps a runtime IsDedicated guard as well as
    // the patch-time gate. See the note on RunMode about why both exist.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(Heightmap))]
    internal static class SeamlessNormalsPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SeamlessNormalsPatch),
                ValConfig.SectionTerrain,
                "Fix Terrain Seams",
                true,
                "Computes terrain lighting normals across zone boundaries instead of per zone. Vanilla " +
                "shades the same ground differently on each side of a 64m zone border, which shows up as " +
                "a hard crease running through flat terrain - most noticeable in the Plains and Meadows.");
        }

        // Reused between rebuilds; a zone heightmap is m_width+1 squared = 65x65 = 4225 vertices.
        private static readonly List<Vector3> NormalBuffer = new List<Vector3>();

        [HarmonyPostfix]
        [HarmonyPatch("RebuildRenderMesh")]
        private static void RebuildRenderMeshPostfix(Heightmap __instance) {
            if (Enabled == null || !Enabled.Value) { return; }

            // Nothing on a dedicated server ever looks at these normals - it builds the render mesh
            // only because the MeshCollider needs the vertices, and collision does not use normals.
            // IsDedicated rather than IsServer: a listen host draws its own terrain and needs the fix.
            if (RunMode.IsDedicated) { return; }

            // The distant LOD meshes are a separate 3x3 grid at a different vertex spacing; leave them
            // on vanilla behaviour rather than pretending they tile with the zone heightmaps.
            if (__instance.IsDistantLod) { return; }

            ApplyNormals(__instance);

            // Refresh the neighbours whether or not our own zone succeeded, and this is the important
            // part: zones are always built at the frontier of the loaded area, so a new zone almost
            // never has all four of its own neighbours yet. What it *does* do is complete the
            // neighbour set of the zone behind it, which had bailed to vanilla normals for exactly
            // that reason and would otherwise stay that way forever - nothing else re-pokes it.
            //
            // Skipping this when our own zone fails leaves a permanent band of vanilla-normal terrain
            // trailing the player, which is what the measurements showed: 81% of shared vertices
            // matching near the player against 43% further out.
            //
            // Recomputing a neighbour is pure arithmetic over 4225 vertices, far cheaper than the mesh
            // rebuild that just happened, so do it eagerly rather than keeping a dirty queue.
            float zoneSize = __instance.m_width * __instance.m_scale;
            RefreshNeighbour(__instance, -zoneSize, 0f);
            RefreshNeighbour(__instance, zoneSize, 0f);
            RefreshNeighbour(__instance, 0f, -zoneSize);
            RefreshNeighbour(__instance, 0f, zoneSize);
        }

        private static void RefreshNeighbour(Heightmap origin, float dx, float dz) {
            Heightmap neighbour = FindNeighbour(origin, dx, dz);
            if (neighbour != null) { ApplyNormals(neighbour); }
        }

        // Returns false when the normals were left untouched, which is the correct outcome whenever we
        // cannot do better than vanilla - a half-applied fix would put a seam one zone further out
        // rather than removing it.
        private static bool ApplyNormals(Heightmap hmap) {
            Mesh mesh = hmap.m_renderMesh;
            if (mesh == null) { return false; }

            int width = hmap.m_width;
            int stride = width + 1;
            if (mesh.vertexCount != stride * stride) { return false; }

            float zoneSize = width * hmap.m_scale;
            Heightmap west = FindNeighbour(hmap, -zoneSize, 0f);
            Heightmap east = FindNeighbour(hmap, zoneSize, 0f);
            Heightmap south = FindNeighbour(hmap, 0f, -zoneSize);
            Heightmap north = FindNeighbour(hmap, 0f, zoneSize);

            // Without every neighbour we cannot sample past all four edges, so leave vanilla's normals
            // in place. The postfix on whichever neighbour loads next will come back and redo this.
            if (west == null || east == null || south == null || north == null) { return false; }

            float inv = 1f / (2f * hmap.m_scale);

            NormalBuffer.Clear();
            for (int y = 0; y < stride; y++) {
                for (int x = 0; x < stride; x++) {
                    float dx = (Sample(hmap, x + 1, y, west, east, south, north)
                              - Sample(hmap, x - 1, y, west, east, south, north)) * inv;
                    float dz = (Sample(hmap, x, y + 1, west, east, south, north)
                              - Sample(hmap, x, y - 1, west, east, south, north)) * inv;

                    NormalBuffer.Add(new Vector3(-dx, 1f, -dz).normalized);
                }
            }

            mesh.SetNormals(NormalBuffer);
            // Vanilla derived tangents from the normals it just replaced, so they have to follow.
            mesh.RecalculateTangents();
            return true;
        }

        // Adjacent heightmaps share their boundary vertices: this heightmap's vertex `width` sits at
        // the same world position as the east neighbour's vertex 0. So one step past our edge is the
        // neighbour's vertex 1, and one step before it is the west neighbour's vertex width-1.
        //
        // Only the four axis-aligned samples are ever needed - a central difference never asks for a
        // diagonal - so there is no case where both coordinates are out of range at once.
        private static float Sample(
            Heightmap hmap, int x, int y, Heightmap west, Heightmap east, Heightmap south, Heightmap north) {
            int width = hmap.m_width;

            if (x < 0) { return WorldHeight(west, width - 1, y); }
            if (x > width) { return WorldHeight(east, 1, y); }
            if (y < 0) { return WorldHeight(south, x, width - 1); }
            if (y > width) { return WorldHeight(north, x, 1); }

            return WorldHeight(hmap, x, y);
        }

        // Heights are stored relative to the heightmap's own transform, so they have to be lifted into
        // world space before two heightmaps can be compared.
        private static float WorldHeight(Heightmap hmap, int x, int y) =>
            hmap.GetHeight(x, y) + hmap.transform.position.y;

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
