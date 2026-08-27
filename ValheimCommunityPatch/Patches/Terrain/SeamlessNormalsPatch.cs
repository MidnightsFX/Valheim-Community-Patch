using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
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
    // Costs of earlier versions of this fix, measured and removed:
    //
    //  - Eager neighbour refresh recomputed the same zone up to five times per rebuild during
    //    generation bursts. Rebuilds now mark a dirty set, and one hook at the end of
    //    MonoUpdaters.LateUpdate (downstream of every Heightmap.CustomLateUpdate rebuild) processes
    //    each affected map exactly once per frame. A rebuild after that point in the frame slips to
    //    the next frame's pass - one frame of vanilla shading, self-correcting.
    //
    //  - Per-sample transform.position.y interop: hoisted to one read per map per pass.
    //
    //  - Unity's RecalculateTangents ran once in vanilla's rebuild and again per ApplyNormals. Both
    //    are gone: the rebuild's call is transpiled into TangentsOrDefer, which skips the pass
    //    whenever this fix will supply tangents later the same frame, and the dirty pass computes
    //    them analytically in the same loop as the normals. For this mesh's planar UV layout
    //    (u along +X, v along +Z everywhere, Heightmap.cs:533) the tangent is the Gram-Schmidt
    //    projection of +X against the vertex normal with w = -1 (bitangent cross(N,T)*w along +Z).
    //    mesh.Clear() in the rebuild wipes tangents, so every deferred map MUST receive tangents
    //    from the pass - a map whose ApplyNormals bails (missing neighbours) gets analytic tangents
    //    computed from the vanilla normals it kept, and TangentsOrDefer only defers at all when the
    //    processing hook is guaranteed to exist (MonoUpdaters alive), so a menu-scene rebuild keeps
    //    Unity's pass. The advanced verify toggle restores Unity's tangents everywhere, compares
    //    them against the analytic formula on a vertex sample, and reports.
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
                ValConfig.SectionDebug,
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
        private static readonly HashSet<Heightmap> RebuiltScratch = new HashSet<Heightmap>();
        private static readonly HashSet<Heightmap> AffectedScratch = new HashSet<Heightmap>();

        private static readonly MethodInfo RecalculateTangentsMethod =
            AccessTools.Method(typeof(Mesh), nameof(Mesh.RecalculateTangents), new System.Type[0]);
        private static readonly MethodInfo TangentsOrDeferMethod =
            AccessTools.Method(typeof(SeamlessNormalsPatch), nameof(TangentsOrDefer));

        // Replaces `this.m_renderMesh.RecalculateTangents()` in Heightmap.RebuildRenderMesh with
        // `TangentsOrDefer(this.m_renderMesh, this)`, so the decision to skip Unity's tangent pass
        // is made at runtime - the toggle stays live, unlike a transpiler that bakes it in.
        // Priority.Last, for the reason in ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("RebuildRenderMesh")]
        private static IEnumerable<CodeInstruction> RebuildRenderMeshTranspiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            int replaced = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (!codes[i].Calls(RecalculateTangentsMethod)) { continue; }

                // The callvirt becomes `ldarg.0` (keeping any labels that target it) and the static
                // call follows: stack [mesh] -> [mesh, this] -> TangentsOrDefer(mesh, heightmap).
                codes[i].opcode = OpCodes.Ldarg_0;
                codes[i].operand = null;
                codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, TangentsOrDeferMethod));
                replaced++;
                i++;
            }

            if (replaced != 1) {
                Logger.LogWarning(
                    $"Heightmap.RebuildRenderMesh: expected 1 RecalculateTangents call, found {replaced}, " +
                    "so the tangent half of the terrain seam fix is inactive. Another mod has most " +
                    "likely already rewritten the method - if so, nothing is wrong.");
                return instructions;
            }

            return codes;
        }

        // Runs where vanilla ran RecalculateTangents. Deferring is only legal when the dirty pass
        // is guaranteed to supply tangents afterwards - same conditions under which the postfix
        // marks the map, plus a live MonoUpdaters to host the pass (absent in the menu scene).
        private static void TangentsOrDefer(Mesh mesh, Heightmap hmap) {
            if (WillProcess(hmap)) { return; }

            mesh.RecalculateTangents();
        }

        private static bool WillProcess(Heightmap hmap) {
            return Enabled != null && Enabled.Value
                && !RunMode.IsDedicated
                && !hmap.IsDistantLod
                && MonoUpdaters.s_instance != null;
        }

        [HarmonyPostfix]
        [HarmonyPatch("RebuildRenderMesh")]
        private static void RebuildRenderMeshPostfix(Heightmap __instance) {
            if (!WillProcess(__instance)) { return; }

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

                if (Enabled == null || !Enabled.Value || RunMode.IsDedicated) {
                    // Marked while enabled, processed while not: these meshes deferred their tangent
                    // pass on the promise that this hook would deliver. Give them Unity's before
                    // standing down.
                    foreach (Heightmap hmap in Dirty) {
                        if (hmap != null && hmap.m_renderMesh != null) { hmap.m_renderMesh.RecalculateTangents(); }
                    }

                    Dirty.Clear();
                    return;
                }

                // Expand each rebuilt map to itself plus its four neighbours, deduped. The neighbour
                // refresh is load-bearing: zones are always built at the frontier of the loaded
                // area, so a new zone almost never has all four of its own neighbours yet. What it
                // *does* do is complete the neighbour set of the zone behind it, which had bailed to
                // vanilla normals for exactly that reason and would otherwise stay that way forever -
                // nothing else re-pokes it. Neighbours are looked up here, at process time, so zones
                // spawned later in the same burst are found.
                RebuiltScratch.Clear();
                AffectedScratch.Clear();

                foreach (Heightmap hmap in Dirty) {
                    if (hmap == null) { continue; }

                    RebuiltScratch.Add(hmap);
                    AffectedScratch.Add(hmap);

                    float zoneSize = hmap.m_width * hmap.m_scale;
                    AddNeighbour(hmap, -zoneSize, 0f);
                    AddNeighbour(hmap, zoneSize, 0f);
                    AddNeighbour(hmap, 0f, -zoneSize);
                    AddNeighbour(hmap, 0f, zoneSize);
                }

                Dirty.Clear();

                foreach (Heightmap hmap in AffectedScratch) {
                    if (ApplyNormals(hmap)) { continue; }

                    // A bailed *rebuilt* map kept vanilla's normals but its tangents were wiped by
                    // mesh.Clear() and deferred to us - compute them from the normals it has. A
                    // bailed neighbour was not rebuilt this frame and keeps its existing tangents.
                    if (RebuiltScratch.Contains(hmap)) { ApplyFallbackTangents(hmap); }
                }

                RebuiltScratch.Clear();
                AffectedScratch.Clear();
            }

            private static void AddNeighbour(Heightmap origin, float dx, float dz) {
                Heightmap neighbour = FindNeighbour(origin, dx, dz);
                if (neighbour != null) { AffectedScratch.Add(neighbour); }
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

        // The one place the Gram-Schmidt tangent formula lives: T = normalize((1,0,0) - N.x*N),
        // w = -1. The hot path, the bail path, and the verify comparison all call this, so a fix
        // to the formula cannot miss one of them.
        private static Vector4 AnalyticTangent(Vector3 normal) {
            float tx = 1f - normal.x * normal.x;
            float ty = -normal.x * normal.y;
            float tz = -normal.x * normal.z;
            float inv = 1f / Mathf.Sqrt(tx * tx + ty * ty + tz * tz);
            return new Vector4(tx * inv, ty * inv, tz * inv, -1f);
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

            float invStep = 1f / (2f * hmap.m_scale);
            bool analyticTangents = VerifyTangents == null || !VerifyTangents.Value;

            NormalBuffer.Clear();
            TangentBuffer.Clear();
            for (int y = 0; y < stride; y++) {
                for (int x = 0; x < stride; x++) {
                    float dx = (Sample(hmap, x + 1, y, west, east, south, north, selfY, westY, eastY, southY, northY)
                              - Sample(hmap, x - 1, y, west, east, south, north, selfY, westY, eastY, southY, northY)) * invStep;
                    float dz = (Sample(hmap, x, y + 1, west, east, south, north, selfY, westY, eastY, southY, northY)
                              - Sample(hmap, x, y - 1, west, east, south, north, selfY, westY, eastY, southY, northY)) * invStep;

                    // Manual inverse-sqrt normalization: Vector3.normalized was a measured cost at
                    // 4225 vertices x 5 maps per burst.
                    float inv = 1f / Mathf.Sqrt(dx * dx + 1f + dz * dz);
                    Vector3 normal = new Vector3(-dx * inv, inv, -dz * inv);
                    NormalBuffer.Add(normal);

                    if (analyticTangents) { TangentBuffer.Add(AnalyticTangent(normal)); }
                }
            }

            mesh.SetNormals(NormalBuffer);

            if (analyticTangents) {
                mesh.SetTangents(TangentBuffer);
            } else {
                mesh.RecalculateTangents();
                CompareTangents(mesh);
            }

            return true;
        }

        // The bail path for a mesh rebuilt this frame: vanilla's normals are on the mesh, tangents
        // were deferred to us. Analytic tangents from those normals are what Unity's pass would
        // produce for this UV layout, at a fraction of the cost.
        private static void ApplyFallbackTangents(Heightmap hmap) {
            Mesh mesh = hmap.m_renderMesh;
            if (mesh == null) { return; }

            if (VerifyTangents != null && VerifyTangents.Value) {
                mesh.RecalculateTangents();
                return;
            }

            NormalBuffer.Clear();
            mesh.GetNormals(NormalBuffer);

            TangentBuffer.Clear();
            for (int i = 0; i < NormalBuffer.Count; i++) { TangentBuffer.Add(AnalyticTangent(NormalBuffer[i])); }

            mesh.SetTangents(TangentBuffer);
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
                Vector4 analytic = AnalyticTangent(NormalBuffer[i]);
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
