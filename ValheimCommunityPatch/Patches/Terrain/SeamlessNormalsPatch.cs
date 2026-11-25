using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // RecalculateNormals sees one zone's mesh only, so border vertices shade differently per side, creasing the grid.
    // Postfixes Heightmap.RebuildRenderMesh and MonoUpdaters.LateUpdate to compute normals across borders once a frame.
    // Ground on either side of a 64 m zone border shades the same, and distant tiles rebuild with cheaper tangents.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Heightmap))]
    internal static class SeamlessNormalsPatch {
        private const string FixName = "Fix Terrain Seams";

        internal static FixToggle Enabled;
        internal static ConfigEntry<bool> VerifyTangents;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SeamlessNormalsPatch),
                ValConfig.SectionTerrain,
                FixName,
                true,
                "Removes the hard lighting crease along zone borders, most visible on flat Plains and Meadows ground.");

            VerifyTangents = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Terrain Tangents",
                false,
                "Diagnostic. Uses Unity's terrain tangents instead of the seam fix's own and logs where the two " +
                "disagree; costs the mesh work the fix saves.",
                advanced: true);
        }

        // A zone heightmap is 65x65 = 4225 vertices; these are reused between rebuilds.
        private static readonly List<Vector3> NormalBuffer = new List<Vector3>();
        private static readonly List<Vector4> TangentBuffer = new List<Vector4>();
        private static readonly List<Vector4> VerifyBuffer = new List<Vector4>();
        private static Vector3[] s_normals = new Vector3[0];
        private static Vector4[] s_tangents = new Vector4[0];

        // World heights of a map and a one-vertex rim taken from its neighbours, (stride + 2) square.
        private static float[] s_grid = new float[0];
        private static float[] s_heights = new float[0];

        // Maps whose render mesh was rebuilt this frame, processed by the LateUpdate hook.
        private static readonly HashSet<Heightmap> Dirty = new HashSet<Heightmap>();
        private static readonly HashSet<Heightmap> RebuiltScratch = new HashSet<Heightmap>();
        private static readonly HashSet<Heightmap> AffectedScratch = new HashSet<Heightmap>();

        private static readonly MethodInfo RecalculateTangentsMethod =
            AccessTools.Method(typeof(Mesh), nameof(Mesh.RecalculateTangents), new System.Type[0]);
        private static readonly MethodInfo TangentsOrDeferMethod =
            AccessTools.Method(typeof(SeamlessNormalsPatch), nameof(TangentsOrDefer));

        // A mod that can skip RebuildRenderMesh with a prefix builds the mesh later, out of this pass's sight, and
        // writes its own normals over these (ValheimOptimized does, on its own threads). Transpilers do not count:
        // the mesh is still built inside the method, and this pass runs after it.
        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            typeof(SeamlessNormalsPatch),
            AccessTools.DeclaredMethod(typeof(Heightmap), "RebuildRenderMesh"),
            transpilers: false,
            owners => $"Terrain mesh building is changed by {owners}, so '{FixName}' stands down and that " +
                      "mod's terrain shading applies.");

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
                PatchHelper.WarnOnce(
                    $"Heightmap.RebuildRenderMesh: expected 1 RecalculateTangents call, found {replaced}, " +
                    "so the tangent half of the terrain seam fix is inactive. Another mod has most " +
                    "likely already rewritten the method - if so, nothing is wrong.");
                return instructions;
            }

            return codes;
        }

        // Deferring is only legal when the dirty pass is guaranteed to supply tangents afterwards. A
        // distant-terrain tile has no seam pass, but the same formula from its fresh normals replaces
        // Unity's tangent pass, which was two thirds of rebuilding one (1.4 of 2.1 ms).
        private static void TangentsOrDefer(Mesh mesh, Heightmap hmap) {
            if (WillProcess(hmap)) { return; }

            if (hmap.IsDistantLod && WillShadeDistant()) {
                ApplyFallbackTangents(hmap);
                return;
            }

            mesh.RecalculateTangents();
        }

        private static bool WillShadeDistant() {
            return Enabled != null && Enabled.Value && !RunMode.IsDedicated && !Takeover.TakenOver;
        }

        // MonoUpdaters hosts the pass and does not exist in the menu scene. The takeover check comes
        // last so it is first asked in a world, after every mod has patched. RebuildRenderMesh also
        // runs on a dedicated server, for the collider, hence the IsDedicated check.
        private static bool WillProcess(Heightmap hmap) {
            return Enabled != null && Enabled.Value
                && !RunMode.IsDedicated
                && !hmap.IsDistantLod
                && MonoUpdaters.s_instance != null
                && !Takeover.TakenOver;
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
                    if (TryApplyNormals(hmap)) { continue; }

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

        // T = normalize(N.y, -N.x, 0), w = -1: the surface's slope along +X. The mesh's u coordinate runs
        // along X, so this is the direction Unity's RecalculateTangents derives, and it is already at right
        // angles to the normal. Measured against Unity's on loaded tiles: mean dot 0.99999. The ApplyNormals
        // loop writes it out inline with the same operations.
        private static Vector4 AnalyticTangent(Vector3 normal) {
            float inv = 1f / Mathf.Sqrt(normal.y * normal.y + normal.x * normal.x);
            return new Vector4(normal.y * inv, -normal.x * inv, 0f, -1f);
        }

        // One map that cannot be read must not end the pass: every rebuilt map after it has deferred
        // its tangents here, and the dirty set they came from is already cleared.
        private static bool TryApplyNormals(Heightmap hmap) {
            try {
                return ApplyNormals(hmap);
            } catch (System.Exception ex) {
                PatchHelper.WarnOnce($"'{FixName}' could not read a terrain tile, which keeps the game's shading: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
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
            if (hmap.m_heights.Count != stride * stride) { return false; }

            int padded = stride + 2;
            FillGrid(hmap, west, east, south, north, width, stride, padded);

            float invStep = 1f / (2f * hmap.m_scale);
            bool analyticTangents = VerifyTangents == null || !VerifyTangents.Value;

            int count = stride * stride;
            if (s_normals.Length != count) {
                s_normals = new Vector3[count];
                s_tangents = new Vector4[count];
            }

            // Mono's JIT inlines neither Mathf.Sqrt nor AnalyticTangent, so both are written out here with
            // the same float operations (Mathf.Sqrt is (float)Math.Sqrt). The (float) casts on the sums are
            // needed: without them Mono adds in double precision on the way to Math.Sqrt, which changed the
            // last bit of one normal in eight. Measured per 65x65 tile: 0.52 ms for the old per-sample
            // version, 0.31 with Mathf.Sqrt, 0.23 like this.
            float[] grid = s_grid;
            Vector3[] normals = s_normals;
            Vector4[] tangents = s_tangents;
            for (int y = 0; y < stride; y++) {
                int row = (y + 1) * padded + 1;
                int o = y * stride;
                for (int x = 0; x < stride; x++) {
                    int c = row + x;
                    float dx = (grid[c + 1] - grid[c - 1]) * invStep;
                    float dz = (grid[c + padded] - grid[c - padded]) * invStep;

                    float inv = 1f / (float)System.Math.Sqrt((float)(dx * dx + 1f + dz * dz));
                    float nx = -dx * inv;
                    float nz = -dz * inv;
                    normals[o + x] = new Vector3(nx, inv, nz);

                    if (analyticTangents) {
                        float tinv = 1f / (float)System.Math.Sqrt((float)(inv * inv + nx * nx));
                        tangents[o + x] = new Vector4(inv * tinv, -nx * tinv, 0f, -1f);
                    }
                }
            }

            mesh.SetNormals(normals);

            if (analyticTangents) {
                mesh.SetTangents(tangents);
            } else {
                mesh.RecalculateTangents();
                CompareTangents(mesh);
            }

            return true;
        }

        // Heights are relative to each heightmap's own transform, so every value gets its map's world
        // offset. Adjacent heightmaps share their boundary vertices: this map's vertex `width` is the east
        // neighbour's vertex 0, so one step past our edge is the neighbour's vertex 1 and one step before
        // it is the west neighbour's vertex width-1. A central difference never asks for a diagonal, so
        // the rim's corners stay unused.
        private static void FillGrid(
            Heightmap hmap, Heightmap west, Heightmap east, Heightmap south, Heightmap north,
            int width, int stride, int padded) {
            if (s_grid.Length != padded * padded) { s_grid = new float[padded * padded]; }
            if (s_heights.Length != stride * stride) { s_heights = new float[stride * stride]; }

            float selfY = hmap.transform.position.y;
            float westY = west.transform.position.y;
            float eastY = east.transform.position.y;
            float southY = south.transform.position.y;
            float northY = north.transform.position.y;

            hmap.m_heights.CopyTo(s_heights);
            for (int y = 0; y < stride; y++) {
                int row = (y + 1) * padded;
                for (int x = 0; x < stride; x++) { s_grid[row + x + 1] = s_heights[y * stride + x] + selfY; }

                s_grid[row] = west.GetHeight(width - 1, y) + westY;
                s_grid[row + stride + 1] = east.GetHeight(1, y) + eastY;
            }

            for (int x = 0; x < stride; x++) {
                s_grid[x + 1] = south.GetHeight(x, width - 1) + southY;
                s_grid[(stride + 1) * padded + x + 1] = north.GetHeight(x, 1) + northY;
            }
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
            if (VerifyBuffer.Count != s_normals.Length) { return; }

            int divergent = 0;
            float worstDot = 1f;
            for (int i = 0; i < VerifyBuffer.Count; i += 173) {
                Vector4 analytic = AnalyticTangent(s_normals[i]);
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

        // Zone heightmaps are exactly zoneSize apart, so offsetting by that lands on the
        // neighbour's centre.
        private static Heightmap FindNeighbour(Heightmap hmap, float dx, float dz) {
            Heightmap neighbour = Heightmap.FindHeightmap(hmap.transform.position + new Vector3(dx, 0f, dz));

            if (neighbour == null || neighbour == hmap || neighbour.IsDistantLod) { return null; }
            if (neighbour.m_width != hmap.m_width || neighbour.m_scale != hmap.m_scale) { return null; }

            // Not built yet: a mod can defer the build past OnEnable (Fires Ghetto Networking's Spread Terrain
            // Rebuilds), and GetHeight checks the grid coordinates but not the list behind them. The build's own
            // rebuild later marks the map dirty, which redoes the maps around it.
            if (!HeightmapSampling.IsBuilt(neighbour)) { return null; }

            return neighbour;
        }
    }
}
