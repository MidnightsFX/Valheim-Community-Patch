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
    // Two costs of the original version of this fix were measured and removed:
    //
    //  - It refreshed all four neighbours eagerly inside every RebuildRenderMesh postfix. During a
    //    generation burst - a frontier sprint, a TerrainComp double-rebuild - the same zone was
    //    recomputed up to five times in one frame. Rebuilds now only mark a dirty set, and one hook
    //    at the end of MonoUpdaters.LateUpdate (which is where every Heightmap.CustomLateUpdate
    //    rebuild runs, so it is downstream of all of them) processes each affected map exactly once
    //    per frame. A rebuild triggered by another script's LateUpdate after that point slips to the
    //    next frame - one frame of vanilla normals, self-correcting.
    //
    //  - WorldHeight read hmap.transform.position.y per height sample: 4225 vertices x 4 samples of
    //    native interop per map. The five offsets are now hoisted to one read per map per pass.
    //
    // Tangents: vanilla derives them from the normals it just computed, so replacing the normals
    // means the tangents have to follow. Unity's generic RecalculateTangents was itself a measured
    // cost, and for this mesh it is overkill: RebuildRenderMesh maps UVs planarly - u along +X, v
    // along +Z, everywhere (Heightmap.cs:533) - so the tangent is just the Gram-Schmidt projection
    // of +X against the vertex normal, T = normalize((1,0,0) - N.x*N) with w = -1 (bitangent
    // cross(N, T) * w must point along +Z). Computed in the same loop as the normals. The advanced
    // verify toggle runs Unity's version instead, compares a sample, and reports - flip it on if a
    // lighting artifact is ever suspected here.
    //
    // Client: normals are shading only, and collision does not use them. This is one of the two
    // fixes whose target method genuinely runs and does real work on a dedicated server - the render
    // mesh is built there for the MeshCollider - so it keeps a runtime IsDedicated guard as well as
    // the patch-time gate. See the note on RunMode about why both exist.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(Heightmap))]
    internal static class SeamlessNormalsPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> VerifyTangents;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SeamlessNormalsPatch),
                ValConfig.SectionTerrain,
                "Fix Terrain Seams",
                true,
                "Computes terrain lighting normals across zone boundaries instead of per zone. Vanilla " +
                "shades the same ground differently on each side of a 64m zone border, which shows up as " +
                "a hard crease running through flat terrain - most noticeable in the Plains and Meadows.");

            VerifyTangents = ValConfig.BindServerConfig(
                ValConfig.SectionTerrain,
                "Verify Terrain Tangents",
                false,
                "Diagnostic. Uses Unity's tangent recalculation instead of the analytic tangents the " +
                "seam fix normally computes, compares the two on a sample of vertices, and logs any " +
                "disagreement. Costs the mesh pass the analytic version exists to avoid, so leave it " +
                "off unless terrain lighting looks wrong.",
                advanced: true);
        }

        // Reused between rebuilds; a zone heightmap is m_width+1 squared = 65x65 = 4225 vertices.
        private static readonly List<Vector3> NormalBuffer = new List<Vector3>();
        private static readonly List<Vector4> TangentBuffer = new List<Vector4>();
        private static readonly List<Vector4> VerifyBuffer = new List<Vector4>();

        // Maps whose render mesh was rebuilt this frame. Processed and cleared by the LateUpdate
        // hook below; a map destroyed while marked is removed by the OnDestroy hook rather than
        // waiting to be skipped as Unity-null.
        private static readonly HashSet<Heightmap> Dirty = new HashSet<Heightmap>();
        private static readonly List<Heightmap> ProcessScratch = new List<Heightmap>();
        private static readonly HashSet<Heightmap> AffectedScratch = new HashSet<Heightmap>();

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

            Dirty.Add(__instance);
        }

        // Runs after every Heightmap.CustomLateUpdate of the frame - they all execute inside
        // MonoUpdaters.LateUpdate (MonoUpdaters.cs:82) - and after the Update-phase rebuilds from
        // ZoneSystem.SpawnZone and TerrainComp, so by this point the frame's rebuilds are done.
        [HarmonyPatch(typeof(MonoUpdaters), "LateUpdate")]
        internal static class ProcessDirtyHook {
            [HarmonyPostfix]
            private static void Postfix() {
                if (Dirty.Count == 0) { return; }
                if (Enabled == null || !Enabled.Value || RunMode.IsDedicated) { Dirty.Clear(); return; }

                // Expand each dirty map to itself plus its four neighbours, deduped. The neighbour
                // refresh is load-bearing: zones are always built at the frontier of the loaded
                // area, so a new zone almost never has all four of its own neighbours yet. What it
                // *does* do is complete the neighbour set of the zone behind it, which had bailed to
                // vanilla normals for exactly that reason and would otherwise stay that way forever -
                // nothing else re-pokes it. Neighbours are looked up here, at process time, so zones
                // spawned later in the same burst are found.
                AffectedScratch.Clear();
                ProcessScratch.Clear();

                foreach (Heightmap hmap in Dirty) {
                    if (hmap == null) { continue; }

                    if (AffectedScratch.Add(hmap)) { ProcessScratch.Add(hmap); }

                    float zoneSize = hmap.m_width * hmap.m_scale;
                    AddNeighbour(hmap, -zoneSize, 0f);
                    AddNeighbour(hmap, zoneSize, 0f);
                    AddNeighbour(hmap, 0f, -zoneSize);
                    AddNeighbour(hmap, 0f, zoneSize);
                }

                Dirty.Clear();

                for (int i = 0; i < ProcessScratch.Count; i++) { ApplyNormals(ProcessScratch[i]); }

                AffectedScratch.Clear();
                ProcessScratch.Clear();
            }

            private static void AddNeighbour(Heightmap origin, float dx, float dz) {
                Heightmap neighbour = FindNeighbour(origin, dx, dz);
                if (neighbour != null && AffectedScratch.Add(neighbour)) { ProcessScratch.Add(neighbour); }
            }
        }

        // A destroyed map must leave the dirty set: HashSet hashes by reference so the entry itself
        // is harmless, but leaving it means a frame-boundary race where the Unity-null skip is the
        // only guard. Cheap to do properly.
        [HarmonyPatch(typeof(Heightmap), "OnDestroy")]
        internal static class OnDestroyHook {
            [HarmonyPostfix]
            private static void Postfix(Heightmap __instance) => Dirty.Remove(__instance);
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
            // in place. The dirty pass on whichever neighbour loads next will come back and redo this.
            if (west == null || east == null || south == null || north == null) { return false; }

            // Heights are stored relative to each heightmap's own transform, so cross-map samples
            // need the world offsets - hoisted to one native read per map instead of one per sample.
            float selfY = hmap.transform.position.y;
            float westY = west.transform.position.y;
            float eastY = east.transform.position.y;
            float southY = south.transform.position.y;
            float northY = north.transform.position.y;

            float inv = 1f / (2f * hmap.m_scale);
            bool analyticTangents = VerifyTangents == null || !VerifyTangents.Value;

            NormalBuffer.Clear();
            TangentBuffer.Clear();
            for (int y = 0; y < stride; y++) {
                for (int x = 0; x < stride; x++) {
                    float dx = (Sample(hmap, x + 1, y, west, east, south, north, selfY, westY, eastY, southY, northY)
                              - Sample(hmap, x - 1, y, west, east, south, north, selfY, westY, eastY, southY, northY)) * inv;
                    float dz = (Sample(hmap, x, y + 1, west, east, south, north, selfY, westY, eastY, southY, northY)
                              - Sample(hmap, x, y - 1, west, east, south, north, selfY, westY, eastY, southY, northY)) * inv;

                    Vector3 normal = new Vector3(-dx, 1f, -dz).normalized;
                    NormalBuffer.Add(normal);

                    if (analyticTangents) {
                        // Gram-Schmidt of +X against the normal; see the header for why this equals
                        // what RecalculateTangents produces for this mesh's planar UV layout.
                        Vector3 tangent = new Vector3(1f - normal.x * normal.x, -normal.x * normal.y, -normal.x * normal.z);
                        tangent.Normalize();
                        TangentBuffer.Add(new Vector4(tangent.x, tangent.y, tangent.z, -1f));
                    }
                }
            }

            mesh.SetNormals(NormalBuffer);

            if (analyticTangents) {
                mesh.SetTangents(TangentBuffer);
            } else {
                // Vanilla derived tangents from the normals it just replaced, so they have to follow.
                mesh.RecalculateTangents();
                CompareTangents(mesh);
            }

            return true;
        }

        // Diagnostic path: Unity's tangents are on the mesh; recompute the analytic ones for a sample
        // of vertices and report how far apart they are. 173 is coprime with 4225, so the sample walks
        // the whole grid rather than a single column.
        private static void CompareTangents(Mesh mesh) {
            VerifyBuffer.Clear();
            mesh.GetTangents(VerifyBuffer);
            if (VerifyBuffer.Count != NormalBuffer.Count) { return; }

            int divergent = 0;
            float worstDot = 1f;
            for (int i = 0; i < VerifyBuffer.Count; i += 173) {
                Vector3 normal = NormalBuffer[i];
                Vector3 analytic = new Vector3(1f - normal.x * normal.x, -normal.x * normal.y, -normal.x * normal.z).normalized;
                Vector4 unity = VerifyBuffer[i];

                float dot = analytic.x * unity.x + analytic.y * unity.y + analytic.z * unity.z;
                if (dot < worstDot) { worstDot = dot; }
                if (dot < 0.99f || unity.w > 0f) { divergent++; }
            }

            if (divergent == 0) {
                Logger.LogDebug($"Tangent verify: agreed (worst dot {worstDot:F4}).");
            } else {
                Logger.LogWarning(
                    $"Tangent verify: {divergent} sampled vertex(es) diverged (worst dot {worstDot:F4}). " +
                    "Unity's tangents were used. Please report this.");
            }
        }

        // Adjacent heightmaps share their boundary vertices: this heightmap's vertex `width` sits at
        // the same world position as the east neighbour's vertex 0. So one step past our edge is the
        // neighbour's vertex 1, and one step before it is the west neighbour's vertex width-1.
        //
        // Only the four axis-aligned samples are ever needed - a central difference never asks for a
        // diagonal - so there is no case where both coordinates are out of range at once.
        private static float Sample(
            Heightmap hmap, int x, int y, Heightmap west, Heightmap east, Heightmap south, Heightmap north,
            float selfY, float westY, float eastY, float southY, float northY) {
            int width = hmap.m_width;

            if (x < 0) { return west.GetHeight(width - 1, y) + westY; }
            if (x > width) { return east.GetHeight(1, y) + eastY; }
            if (y < 0) { return south.GetHeight(x, width - 1) + southY; }
            if (y > width) { return north.GetHeight(x, 1) + northY; }

            return hmap.GetHeight(x, y) + selfY;
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
