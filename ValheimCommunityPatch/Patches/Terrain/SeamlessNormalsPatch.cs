using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Fix Terrain Seams: terrain lighting normals are computed across zone boundaries, so the
    // ground on either side of a 64 m border is shaded the same.
    //
    // Heightmap.RebuildRenderMesh uses Mesh.RecalculateNormals, which averages only the triangles
    // in that zone's own mesh. A boundary vertex exists in both neighbouring meshes and gets a
    // different normal from each side, which draws as a hard crease along every zone border. The
    // geometry itself already lines up; only the shading disagrees.
    //
    // Rebuilt maps are collected in a dirty set, and one pass at the end of MonoUpdaters.LateUpdate
    // processes each affected map (the rebuilt ones plus their four neighbours) once per frame. For
    // each map with all four neighbours loaded, normals are computed analytically by central
    // difference over the height data, reading across the boundary into the neighbour where the
    // sample falls outside the map; a map missing a neighbour keeps vanilla's normals and is redone
    // when that neighbour loads. Tangents are computed in the same loop (the mesh has a planar UV
    // layout, so the tangent is +X projected against the normal, w = -1), and a transpiler skips
    // vanilla's RecalculateTangents whenever this pass will supply them later in the frame.
    //
    // Client: normals are shading only. The target method runs on a dedicated server too, for the
    // collider, so a runtime IsDedicated guard backs the patch-time gate.
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

        // A zone heightmap is 65x65 = 4225 vertices; these are reused between rebuilds.
        private static readonly List<Vector3> NormalBuffer = new List<Vector3>();
        private static readonly List<Vector4> TangentBuffer = new List<Vector4>();
        private static readonly List<Vector4> VerifyBuffer = new List<Vector4>();

        // Maps whose render mesh was rebuilt this frame, processed by the LateUpdate hook.
        private static readonly HashSet<Heightmap> Dirty = new HashSet<Heightmap>();
        private static readonly HashSet<Heightmap> RebuiltScratch = new HashSet<Heightmap>();
        private static readonly HashSet<Heightmap> AffectedScratch = new HashSet<Heightmap>();

        private static readonly MethodInfo RecalculateTangentsMethod =
            AccessTools.Method(typeof(Mesh), nameof(Mesh.RecalculateTangents), new System.Type[0]);
        private static readonly MethodInfo TangentsOrDeferMethod =
            AccessTools.Method(typeof(SeamlessNormalsPatch), nameof(TangentsOrDefer));

        // Replaces `this.m_renderMesh.RecalculateTangents()` with `TangentsOrDefer(this.m_renderMesh, this)`
        // so the decision to skip Unity's tangent pass is made at runtime and the toggle stays live.
        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("RebuildRenderMesh")]
        private static IEnumerable<CodeInstruction> RebuildRenderMeshTranspiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            int replaced = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (!codes[i].Calls(RecalculateTangentsMethod)) { continue; }

                // The callvirt becomes ldarg.0 (keeping any labels on it) and the static call
                // follows: stack [mesh] -> [mesh, this] -> TangentsOrDefer(mesh, heightmap).
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

        // Deferring is only legal when the dirty pass is guaranteed to supply tangents afterwards.
        private static void TangentsOrDefer(Mesh mesh, Heightmap hmap) {
            if (WillProcess(hmap)) { return; }

            mesh.RecalculateTangents();
        }

        // MonoUpdaters hosts the pass and does not exist in the menu scene.
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

        // Every Heightmap.CustomLateUpdate of the frame runs inside MonoUpdaters.LateUpdate, and
        // the Update-phase rebuilds are earlier still, so by this point the frame's rebuilds are done.
        [HarmonyPatch(typeof(MonoUpdaters), "LateUpdate")]
        internal static class ProcessDirtyHook {
            [HarmonyPostfix]
            private static void Postfix() {
                if (Dirty.Count == 0) { return; }

                if (Enabled == null || !Enabled.Value || RunMode.IsDedicated) {
                    // Marked while enabled, processed while not: these meshes deferred their
                    // tangent pass on the promise this hook would deliver, so give them Unity's.
                    foreach (Heightmap hmap in Dirty) {
                        if (hmap != null && hmap.m_renderMesh != null) { hmap.m_renderMesh.RecalculateTangents(); }
                    }

                    Dirty.Clear();
                    return;
                }

                // Each rebuilt map plus its four neighbours. Zones are built at the frontier of
                // the loaded area, so a new zone rarely has all its neighbours yet; what it does
                // do is complete the neighbour set of the zone behind it, which had kept vanilla
                // normals for exactly that reason and would otherwise stay that way.
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

                    // A rebuilt map that bailed kept vanilla's normals, but its tangents were
                    // wiped by the rebuild and deferred to us. A neighbour that bailed was not
                    // rebuilt and keeps its tangents.
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

        [HarmonyPatch(typeof(Heightmap), "OnDestroy")]
        internal static class OnDestroyHook {
            [HarmonyPostfix]
            private static void Postfix(Heightmap __instance) => Dirty.Remove(__instance);
        }

        // T = normalize((1,0,0) - N.x * N), w = -1. The one place the formula lives.
        private static Vector4 AnalyticTangent(Vector3 normal) {
            float tx = 1f - normal.x * normal.x;
            float ty = -normal.x * normal.y;
            float tz = -normal.x * normal.z;
            float inv = 1f / Mathf.Sqrt(tx * tx + ty * ty + tz * tz);
            return new Vector4(tx * inv, ty * inv, tz * inv, -1f);
        }

        // Returns false when the normals were left untouched, which is right whenever we cannot
        // do better than vanilla: a half-applied fix would move the seam one zone out.
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

            if (west == null || east == null || south == null || north == null) { return false; }

            // Heights are relative to each heightmap's own transform, so cross-map samples need
            // the world offsets; one transform read per map rather than one per sample.
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

        // Analytic tangents from the normals already on the mesh, for a rebuilt map that bailed.
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

        // Diagnostic: Unity's tangents are on the mesh; compare a sample against the analytic
        // ones. 173 is coprime with 4225, so the sample walks the whole grid.
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

        // Adjacent heightmaps share their boundary vertices: this map's vertex `width` is the east
        // neighbour's vertex 0, so one step past our edge is the neighbour's vertex 1 and one step
        // before it is the west neighbour's vertex width-1. A central difference never asks for a
        // diagonal, so both coordinates are never out of range at once.
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
