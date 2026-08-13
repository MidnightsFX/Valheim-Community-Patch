using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Terrain {
    // Read-only diagnostic: `vcp_terrainscan [radius]`.
    //
    // Adjacent zone heightmaps share their boundary vertices - this heightmap's vertex `width` is at
    // the same world position as the east neighbour's vertex 0. Anything the two sides disagree about
    // at those vertices shows up in game as a seam.
    //
    // Three deltas are reported separately because they point at three different causes:
    //
    //   height  - non-zero means the terrain geometry genuinely does not line up, which would mean
    //             player terrain edits diverged across the zone boundary (TerrainComp stores deltas
    //             relative to the current height and routes them through whichever peer owns each
    //             zone, so two owners can resolve the same shared vertex against different snapshots)
    //   normal  - non-zero with a zero height delta means the geometry is fine and the seam is purely
    //             a lighting artifact, which is what SeamlessNormalsPatch addresses
    //   paint   - reported per channel. RGB is dirt / cultivated / paved; alpha is the cleared
    //             vegetation mask, which also drives lava rendering in the Ashlands. Knowing which
    //             channel disagrees says whether the cause is player painting or the vegetation mask.
    //
    // Normals are bucketed by whether *both* zones of a pair had all four of their own neighbours
    // loaded, since that is the precondition for SeamlessNormalsPatch doing anything. Without that
    // split, terrain at the edge of the loaded area drags the numbers down and makes a working fix
    // look broken.
    //
    // It never calls Poke, Save or touches TerrainComp data. Running it changes nothing.
    [HarmonyPatch(typeof(Terminal))]
    internal static class TerrainScanCommand {
        private const string CommandName = "vcp_terrainscan";
        private const float DefaultRadius = 200f;

        // Vertices closer than this are treated as agreeing; well under anything visible.
        private const float HeightEpsilon = 0.001f;
        private const float NormalEpsilon = 0.001f;
        private const float PaintEpsilon = 0.004f;

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Terminal.InitTerminal))]
        [HarmonyPriority(Priority.First)]
        private static void InitTerminalPrefix(out bool __state) {
            __state = Terminal.m_terminalInitialized;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(Terminal.InitTerminal))]
        private static void InitTerminalPostfix(bool __state) {
            if (__state) { return; }

            try {
                new Terminal.ConsoleCommand(
                    CommandName,
                    "[radius] - reports terrain mismatches between adjacent zones (read-only)",
                    Run,
                    isCheat: false, isNetwork: false, onlyServer: false);
            } catch (Exception ex) {
                // Another mod registering the same name should not take the rest of the patch down.
                Logger.LogWarning($"Could not register the {CommandName} command: {ex.Message}");
            }
        }

        private static void Run(Terminal.ConsoleEventArgs args) {
            if (Player.m_localPlayer == null) {
                args.Context.AddString("No local player.");
                return;
            }

            float radius = DefaultRadius;
            if (args.Length > 1 && !float.TryParse(args[1], out radius)) {
                args.Context.AddString($"Syntax: {CommandName} [radius]");
                return;
            }

            Vector3 origin = Player.m_localPlayer.transform.position;

            List<Heightmap> nearby = new List<Heightmap>();
            foreach (Heightmap hmap in Heightmap.GetAllHeightmaps()) {
                if (hmap != null && !hmap.IsDistantLod && Utils.DistanceXZ(hmap.transform.position, origin) <= radius) {
                    nearby.Add(hmap);
                }
            }

            if (nearby.Count == 0) {
                args.Context.AddString("No loaded heightmaps in range.");
                return;
            }

            // mesh.normals allocates a fresh array per call, so fetch each heightmap's once. Same for
            // the eligibility test, which walks the loaded heightmap list four times per zone.
            Dictionary<Heightmap, Vector3[]> normals = new Dictionary<Heightmap, Vector3[]>();
            Dictionary<Heightmap, bool> eligible = new Dictionary<Heightmap, bool>();
            int eligibleCount = 0;

            foreach (Heightmap hmap in nearby) {
                Mesh mesh = hmap.m_renderMesh;
                normals[hmap] = mesh != null ? mesh.normals : null;

                bool canBeSeamless = HasAllNeighbours(hmap);
                eligible[hmap] = canBeSeamless;
                if (canBeSeamless) { eligibleCount++; }
            }

            Stats height = new Stats();
            Stats normalBothEligible = new Stats();
            Stats normalFellBack = new Stats();
            Stats[] paint = { new Stats(), new Stats(), new Stats(), new Stats() };
            int pairs = 0;

            foreach (Heightmap hmap in nearby) {
                float zoneSize = hmap.m_width * hmap.m_scale;

                // Only look east and north so each pair is visited once.
                if (ComparePair(hmap, Neighbour(hmap, zoneSize, 0f), true,
                        normals, eligible, height, normalBothEligible, normalFellBack, paint)) { pairs++; }
                if (ComparePair(hmap, Neighbour(hmap, 0f, zoneSize), false,
                        normals, eligible, height, normalBothEligible, normalFellBack, paint)) { pairs++; }
            }

            args.Context.AddString($"--- {CommandName}: {nearby.Count} heightmap(s), {pairs} shared edge(s), radius {radius:0} ---");

            if (pairs == 0) {
                args.Context.AddString("No adjacent loaded pairs to compare. Try a larger radius.");
                return;
            }

            args.Context.AddString($"seamless-eligible: {eligibleCount}/{nearby.Count} zones have all 4 neighbours loaded");
            args.Context.AddString(height.Describe("height", "m", HeightEpsilon));
            args.Context.AddString(normalBothEligible.Describe("normal (both eligible)", "", NormalEpsilon));
            args.Context.AddString(normalFellBack.Describe("normal (edge of loaded area)", "", NormalEpsilon));
            args.Context.AddString(
                $"paint r/g/b/a max: {paint[0].Worst:0.###} / {paint[1].Worst:0.###} / " +
                $"{paint[2].Worst:0.###} / {paint[3].Worst:0.###}");
            args.Context.AddString(paint[0].Describe("paint dirt (r)", "", PaintEpsilon));
            args.Context.AddString(paint[1].Describe("paint cultivated (g)", "", PaintEpsilon));
            args.Context.AddString(paint[2].Describe("paint paved (b)", "", PaintEpsilon));
            args.Context.AddString(paint[3].Describe("paint vegetation (a)", "", PaintEpsilon));

            if (height.Worst > HeightEpsilon) {
                args.Context.AddString($"Worst height mismatch at {height.WorstAt} ({height.Worst:0.####} m) - geometry, not shading.");
            } else {
                args.Context.AddString("Geometry lines up exactly; any seam here is shading or paint, not terrain height.");
            }
        }

        // The exact precondition SeamlessNormalsPatch checks before it will touch a heightmap.
        private static bool HasAllNeighbours(Heightmap hmap) {
            float zoneSize = hmap.m_width * hmap.m_scale;

            return Neighbour(hmap, -zoneSize, 0f) != null
                && Neighbour(hmap, zoneSize, 0f) != null
                && Neighbour(hmap, 0f, -zoneSize) != null
                && Neighbour(hmap, 0f, zoneSize) != null;
        }

        private static Heightmap Neighbour(Heightmap hmap, float dx, float dz) {
            Heightmap n = Heightmap.FindHeightmap(hmap.transform.position + new Vector3(dx, 0f, dz));

            if (n == null || n == hmap || n.IsDistantLod) { return null; }
            if (n.m_width != hmap.m_width || n.m_scale != hmap.m_scale) { return null; }

            return n;
        }

        // `eastward` picks which axis the shared edge runs along: our vertex (width, i) meets theirs
        // at (0, i) to the east, and our (i, width) meets theirs at (i, 0) to the north.
        private static bool ComparePair(
            Heightmap a, Heightmap b, bool eastward,
            Dictionary<Heightmap, Vector3[]> normals,
            Dictionary<Heightmap, bool> eligible,
            Stats height, Stats normalBothEligible, Stats normalFellBack, Stats[] paint) {
            if (b == null) { return false; }

            normals.TryGetValue(a, out Vector3[] normalsA);
            if (!normals.TryGetValue(b, out Vector3[] normalsB)) {
                normalsB = b.m_renderMesh != null ? b.m_renderMesh.normals : null;
            }

            eligible.TryGetValue(a, out bool eligibleA);
            if (!eligible.TryGetValue(b, out bool eligibleB)) { eligibleB = HasAllNeighbours(b); }
            Stats normalBucket = eligibleA && eligibleB ? normalBothEligible : normalFellBack;

            int width = a.m_width;
            int stride = width + 1;

            for (int i = 0; i <= width; i++) {
                int ax = eastward ? width : i;
                int ay = eastward ? i : width;
                int bx = eastward ? 0 : i;
                int by = eastward ? i : 0;

                Vector3 at = a.transform.position;
                Vector3 worldPos = eastward
                    ? new Vector3(at.x + width * a.m_scale * 0.5f, 0f, at.z + (i - width * 0.5f) * a.m_scale)
                    : new Vector3(at.x + (i - width * 0.5f) * a.m_scale, 0f, at.z + width * a.m_scale * 0.5f);

                float ha = a.GetHeight(ax, ay) + at.y;
                float hb = b.GetHeight(bx, by) + b.transform.position.y;
                height.Add(Mathf.Abs(ha - hb), worldPos);

                if (normalsA != null && normalsB != null) {
                    int ia = ay * stride + ax;
                    int ib = by * stride + bx;
                    if (ia < normalsA.Length && ib < normalsB.Length) {
                        normalBucket.Add((normalsA[ia] - normalsB[ib]).magnitude, worldPos);
                    }
                }

                Color pa = a.GetPaintMask(ax, ay);
                Color pb = b.GetPaintMask(bx, by);
                paint[0].Add(Mathf.Abs(pa.r - pb.r), worldPos);
                paint[1].Add(Mathf.Abs(pa.g - pb.g), worldPos);
                paint[2].Add(Mathf.Abs(pa.b - pb.b), worldPos);
                paint[3].Add(Mathf.Abs(pa.a - pb.a), worldPos);
            }

            return true;
        }

        private sealed class Stats {
            private readonly List<float> _deltas = new List<float>();
            private double _sum;

            internal float Worst { get; private set; }
            internal Vector3 WorstAt { get; private set; }

            internal void Add(float delta, Vector3 at) {
                _deltas.Add(delta);
                _sum += delta;

                if (delta > Worst) {
                    Worst = delta;
                    WorstAt = at;
                }
            }

            internal string Describe(string label, string unit, float epsilon) {
                if (_deltas.Count == 0) { return $"{label}: no samples"; }

                int over = 0;
                for (int i = 0; i < _deltas.Count; i++) {
                    if (_deltas[i] > epsilon) { over++; }
                }

                double mean = _sum / _deltas.Count;
                string verdict = over > 0 ? "MISMATCH" : "ok";
                return $"{label}: max {Worst:0.#####}{unit}  mean {mean:0.#####}{unit}  " +
                       $"{over}/{_deltas.Count} differ  [{verdict}]";
            }
        }
    }
}
