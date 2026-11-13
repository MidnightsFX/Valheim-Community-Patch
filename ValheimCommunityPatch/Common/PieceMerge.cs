using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Stopwatch = System.Diagnostics.Stopwatch;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>A placed piece taken over by Fix Building Renderer Overhead.</summary>
    internal sealed class PieceEntry {
        public WearNTear W;
        // W's instance id, kept so the entry can still be dropped after W is gone.
        public int Id;
        public int Index;
        public PieceCell Cell;
        public readonly List<PiecePart> Parts = new List<PiecePart>();
        public readonly List<MeshRenderer> Renderers = new List<MeshRenderer>();
        public readonly List<PieceLodGroup> Groups = new List<PieceLodGroup>();
        public readonly List<PieceRange> Ranges = new List<PieceRange>();
        public Vector4 UVOffset;
        public int SnowBucket;
        public int Stage;
        // Bumped whenever what the piece contributes to its cell's mesh changes; a build made from an
        // older generation does not draw it.
        public int Gen;
        public bool InMesh;
        public bool Suspended;
        public bool Hidden;
    }

    internal struct PieceRange {
        public PieceGroupMesh Mesh;
        public int Start;
        public int Count;
    }

    internal sealed class PieceGroupMesh {
        public Mesh Mesh;
        public ushort[] Orig;
        public ushort[] Cur;
        public GraphicsBuffer Index;
        public bool Dirty;
    }

    internal sealed class PieceCell {
        public long Key;
        public Vector3 Origin;
        public readonly HashSet<PieceEntry> Pieces = new HashSet<PieceEntry>();
        public bool Stale;
        public float LastChange;
        public float LastBuild = float.MinValue;
        // Pieces gone from the cell whose triangles are still in its meshes, zeroed.
        public int Dead;
        public PieceMerge.PieceBuild Build;
        public GameObject Root;
        public readonly List<PieceGroupMesh> Meshes = new List<PieceGroupMesh>();
    }

    /// <summary>
    /// Merged meshes for Fix Building Renderer Overhead: one mesh per cell, material, layer, shadow mode,
    /// LOD class and snow step, built on a worker thread and swapped in whole.
    /// </summary>
    /// <remarks>
    /// Each cell has a two-level LODGroup whose switch and cull heights are the median of its pieces'
    /// own, scaled from piece size to cell size. A piece's LOD0 goes into the near level and its LOD1
    /// into the far level; a piece with a single level is in both, since its LOD0 lasts until its cull;
    /// renderers outside any LOD level go into meshes that are never culled by distance. Grausten's
    /// per-piece _UVOffset is added to uv0, which is exactly what the shader does with it, and a
    /// piece's snow is carried as one of eight steps by the merged renderer's property block.
    ///
    /// A piece is taken out for a moment (hover highlight, Ashlands glow) or for good (destroyed) by
    /// zeroing its index ranges, so the cell does not have to be rebuilt first; the whole index list
    /// is re-sent once per frame, which every driver takes.
    ///
    /// Zones stream pieces in and out a few at a time as the player moves, so a cell is rebuilt only
    /// for pieces added, once the additions have stopped; a piece that leaves just stays zeroed, and the
    /// cell is rebuilt to drop it only once it holds more zeroed pieces than live ones and has been quiet.
    /// </remarks>
    internal sealed class PieceMerge {
        internal const int MaxVertices = 65000;
        private const int SnowSteps = 8;
        private const int BuildsInFlight = 2;
        private const float FirstBuildDelay = 1.5f;
        private const float RebuildDelay = 3f;
        private const float MinRebuildInterval = 5f;
        private const float ReclaimDelay = 10f;

        private static readonly int SnowLevel = Shader.PropertyToID("_SnowLevel");

        [StructLayout(LayoutKind.Sequential)]
        internal struct PackedVertex {
            public Vector3 Pos;
            public short Nx, Ny, Nz, Nw;
            public short Tx, Ty, Tz, Tw;
            public Vector2 UV;
        }

        private static readonly VertexAttributeDescriptor[] Layout = {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm16, 4),
            new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.SNorm16, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
        };

        private const MeshUpdateFlags Quiet = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds |
                                              MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds;

        private readonly float _cellSize;
        private readonly Dictionary<long, PieceCell> _cells = new Dictionary<long, PieceCell>();
        private readonly List<PieceBuild> _building = new List<PieceBuild>();
        private readonly List<PieceGroupMesh> _dirty = new List<PieceGroupMesh>();
        private readonly List<PieceCell> _empty = new List<PieceCell>();
        private GameObject _root;
        private bool? _gpuIndex;

        /// <summary>Pieces found to have a mesh that can't be read; the caller gives them back to the game.</summary>
        internal readonly List<PieceEntry> Unreadable = new List<PieceEntry>();

        internal int Cells => _cells.Count;
        internal int MeshCount;
        internal long VertexCount;
        internal int BuildsDone;
        internal double WorstApplyMs;

        internal PieceMerge(float cellSize) {
            _cellSize = cellSize;
        }

        internal PieceCell CellFor(Vector3 position) {
            long key = ((long)Mathf.FloorToInt(position.x / _cellSize) << 32) ^ (uint)Mathf.FloorToInt(position.z / _cellSize);
            if (!_cells.TryGetValue(key, out PieceCell cell)) {
                cell = new PieceCell {
                    Key = key,
                    Origin = new Vector3((int)(key >> 32) * _cellSize, 0f, (int)(uint)key * _cellSize),
                };
                _cells[key] = cell;
            }
            return cell;
        }

        internal static void Touch(PieceCell cell) {
            cell.Stale = true;
            cell.LastChange = Time.unscaledTime;
        }

        internal static int SnowStep(float snow) => snow <= 0f ? 0 : Mathf.Clamp(Mathf.RoundToInt(snow * SnowSteps), 1, SnowSteps);

        /// <summary>Hides the piece's own renderers exactly while its merged copy is drawn.</summary>
        internal static void SyncHidden(PieceEntry e) {
            bool hide = e.InMesh && !e.Suspended;
            if (hide == e.Hidden) { return; }
            e.Hidden = hide;
            foreach (MeshRenderer r in e.Renderers) {
                if (r != null) { r.forceRenderingOff = hide; }
            }
            // Unity still evaluates the LODGroups of hidden renderers every frame. A group that also holds a
            // renderer the merge leaves alone would draw all its levels at once if switched off.
            foreach (PieceLodGroup g in e.Groups) {
                if (g.Group != null && g.Owned) { g.Group.enabled = !hide; }
            }
        }

        internal void SetRanges(PieceEntry e, bool visible) {
            foreach (PieceRange r in e.Ranges) {
                PieceGroupMesh m = r.Mesh;
                if (m.Mesh == null) { continue; }
                if (visible) {
                    Array.Copy(m.Orig, r.Start, m.Cur, r.Start, r.Count);
                } else {
                    Array.Clear(m.Cur, r.Start, r.Count);
                }
                MarkDirty(m);
            }
        }

        /// <summary>
        /// Takes the piece out of its cell's mesh for good (destroyed, released or re-collected) by zeroing it;
        /// the cell is not rebuilt for it.
        /// </summary>
        internal void Detach(PieceEntry e) {
            bool inMesh = e.InMesh;
            if (inMesh) { SetRanges(e, false); }
            e.InMesh = false;
            e.Ranges.Clear();
            SyncHidden(e);
            e.Gen++;
            PieceCell cell = e.Cell;
            if (cell == null) { return; }
            e.Cell = null;
            cell.Pieces.Remove(e);
            if (inMesh) { cell.Dead++; }
            if (cell.Pieces.Count == 0) {
                // Dropped at the next tick, without a build.
                cell.Stale = true;
                cell.LastChange = float.MinValue;
            } else if (!cell.Stale) {
                cell.LastChange = Time.unscaledTime;
            }
        }

        private void MarkDirty(PieceGroupMesh m) {
            if (m.Dirty) { return; }
            m.Dirty = true;
            _dirty.Add(m);
        }

        /// <summary>Sends changed index lists to the GPU. Once per frame, before rendering.</summary>
        internal void Flush() {
            foreach (PieceGroupMesh m in _dirty) {
                m.Dirty = false;
                if (m.Mesh == null) { continue; }
                if (m.Index != null) {
                    m.Index.SetData(m.Cur);
                } else {
                    m.Mesh.SetIndexBufferData(m.Cur, 0, 0, m.Cur.Length, Quiet);
                }
            }
            _dirty.Clear();
        }

        /// <summary>
        /// Applies finished builds and starts worker builds for cells that are due, while
        /// <paramref name="left"/> says the frame's budget lasts; drops cells left empty.
        /// </summary>
        internal void Tick(Func<bool> left) {
            for (int i = 0; i < _building.Count; i++) {
                PieceBuild b = _building[i];
                if (!b.Done) { continue; }
                if (b.Error != null || b.Cell.Build != b) {
                    if (b.Error != null) { Logger.LogWarning($"Fix Building Renderer Overhead: a cell build failed ({b.Error.Message}); it is rebuilt."); }
                    Discard(b);
                    _building.RemoveAt(i--);
                    Touch(b.Cell);
                    continue;
                }
                bool finished = Apply(b, left);
                if (finished) {
                    _building.RemoveAt(i--);
                    BuildsDone++;
                }
                if (!left()) { return; }
            }

            if (_building.Count >= BuildsInFlight) { return; }
            float now = Time.unscaledTime;
            _empty.Clear();
            foreach (PieceCell cell in _cells.Values) {
                if (cell.Build != null) { continue; }
                if (cell.Pieces.Count == 0) {
                    if (cell.Stale) { _empty.Add(cell); }
                    continue;
                }
                if (!Due(cell, now)) { continue; }
                if (!left() || _building.Count >= BuildsInFlight) { break; }
                PieceBuild b = Snapshot(cell);
                if (b == null) { continue; }
                cell.Stale = false;
                cell.Dead = 0;
                cell.LastBuild = now;
                cell.Build = b;
                _building.Add(b);
                ThreadPool.QueueUserWorkItem(_ => b.Run());
            }
            foreach (PieceCell cell in _empty) {
                DestroyCellObjects(cell);
                _cells.Remove(cell.Key);
            }
        }

        // Pieces still arriving keep a cell waiting, so a zone loading in is built once; pieces not yet in a
        // mesh are drawn by their own renderers meanwhile.
        private static bool Due(PieceCell cell, float now) {
            if (!cell.Stale) {
                return cell.Dead > cell.Pieces.Count && now - cell.LastChange >= ReclaimDelay;
            }
            if (cell.Root == null) { return now - cell.LastChange >= FirstBuildDelay; }
            return now - cell.LastChange >= RebuildDelay && now - cell.LastBuild >= MinRebuildInterval;
        }

        // Main thread: what the worker needs, copied. Null while a mesh is still being read.
        private PieceBuild Snapshot(PieceCell cell) {
            bool pending = false;
            foreach (PieceEntry e in cell.Pieces) {
                foreach (PiecePart p in e.Parts) {
                    if (PieceMeshCopy.Get(p.Mesh, out bool failed) != null) { continue; }
                    if (failed) {
                        if (!Unreadable.Contains(e)) { Unreadable.Add(e); }
                    } else {
                        pending = true;
                    }
                }
            }
            if (pending || Unreadable.Count > 0) {
                cell.LastChange = Time.unscaledTime - (cell.Root == null ? FirstBuildDelay : RebuildDelay) + 0.25f;
                return null;
            }

            PieceBuild b = new PieceBuild { Cell = cell };
            List<float> switchPerSize = new List<float>(), cullPerSize = new List<float>();

            foreach (PieceEntry e in cell.Pieces) {
                int member = b.Members.Count;
                b.Members.Add(e);
                b.Gens.Add(e.Gen);
                foreach (PieceLodGroup g in e.Groups) {
                    if (g.WorldSize <= 0f) { continue; }
                    if (g.Heights.Length > 1) { switchPerSize.Add(g.Heights[0] / g.WorldSize); }
                    cullPerSize.Add(g.Heights[g.Heights.Length - 1] / g.WorldSize);
                }
                foreach (PiecePart p in e.Parts) {
                    Vector2 st = p.Material.HasProperty("_MainTex") ? p.Material.mainTextureScale : Vector2.one;
                    PieceBuild.Item item = new PieceBuild.Item {
                        Member = member,
                        Data = PieceMeshCopy.Get(p.Mesh, out _),
                        Submesh = p.Submesh,
                        Matrix = Matrix4x4.Translate(-cell.Origin) * p.LocalToWorld,
                        UVShift = new Vector2(st.x != 0f ? e.UVOffset.x / st.x : 0f, st.y != 0f ? e.UVOffset.y / st.y : 0f),
                    };
                    PieceBuild.Key key = new PieceBuild.Key {
                        Material = b.MaterialIndex(p.Material),
                        Layer = p.Renderer.gameObject.layer,
                        Shadow = (int)p.Renderer.shadowCastingMode,
                        Receive = p.Renderer.receiveShadows,
                        Snow = e.SnowBucket,
                    };
                    b.Like[key.Material] = p.Renderer;
                    if (p.Lod == null) {
                        key.Class = 2;
                        b.Add(key, item);
                    } else if (p.Lod.Heights.Length == 1) {
                        key.Class = 0;
                        b.Add(key, item);
                        key.Class = 1;
                        b.Add(key, item);
                    } else {
                        key.Class = p.Level == 0 ? 0 : 1;
                        b.Add(key, item);
                    }
                }
            }

            b.SwitchPerSize = Median(switchPerSize, 0.025f);
            b.CullPerSize = Median(cullPerSize, 0.007f);
            return b;
        }

        private static float Median(List<float> v, float fallback) {
            if (v.Count == 0) { return fallback; }
            v.Sort();
            return v[v.Count / 2];
        }

        // Turns finished chunks into meshes one at a time, then swaps the cell over. True when done.
        private bool Apply(PieceBuild b, Func<bool> left) {
            PieceCell cell = b.Cell;
            if (b.Root == null) {
                if (_root == null) { _root = new GameObject("VCP merged buildings"); }
                b.Root = new GameObject("cell");
                b.Root.SetActive(false);
                b.Root.transform.SetParent(_root.transform, false);
                b.Root.transform.position = cell.Origin;
            }

            while (b.Applied < b.Chunks.Count) {
                long t = Stopwatch.GetTimestamp();
                b.Meshes.Add(MakeMesh(b, b.Chunks[b.Applied]));
                b.Applied++;
                WorstApplyMs = Math.Max(WorstApplyMs, (Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency);
                if (b.Applied < b.Chunks.Count && !left()) { return false; }
            }

            if (b.Near.Count + b.Far.Count > 0) {
                LODGroup lg = b.Root.AddComponent<LODGroup>();
                lg.SetLODs(new[] { new LOD(0.5f, b.Near.ToArray()), new LOD(0.1f, b.Far.ToArray()) });
                lg.RecalculateBounds();
                float near = Mathf.Clamp(b.SwitchPerSize * lg.size, 0.0002f, 0.999f);
                float cull = Mathf.Clamp(b.CullPerSize * lg.size, 0.0001f, near * 0.999f);
                lg.SetLODs(new[] { new LOD(near, b.Near.ToArray()), new LOD(cull, b.Far.ToArray()) });
            }

            Swap(b);
            return true;
        }

        private PieceGroupMesh MakeMesh(PieceBuild b, PieceBuild.Chunk c) {
            Mesh mesh = new Mesh { name = "VCP merged" };
            mesh.SetVertexBufferParams(c.VertexCount, Layout);
            mesh.SetVertexBufferData(c.Vertices, 0, 0, c.VertexCount, 0, Quiet);
            VertexPool.Return(c.Vertices);
            c.Vertices = null;
            mesh.SetIndexBufferParams(c.Indices.Length, IndexFormat.UInt16);
            mesh.SetIndexBufferData(c.Indices, 0, 0, c.Indices.Length, Quiet);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, c.Indices.Length) { bounds = c.Bounds, vertexCount = c.VertexCount }, Quiet);
            mesh.bounds = c.Bounds;

            PieceGroupMesh gm = new PieceGroupMesh { Mesh = mesh, Orig = c.Indices, Cur = (ushort[])c.Indices.Clone() };
            if (_gpuIndex != false) {
                try {
                    mesh.UploadMeshData(true);
                    gm.Index = mesh.GetIndexBuffer();
                    _gpuIndex = true;
                } catch (Exception e) {
                    // Keeps the mesh's CPU copy, and with it the slower index upload through the Mesh API.
                    if (_gpuIndex == null) { Logger.LogDebug($"Fix Building Renderer Overhead: writing index buffers directly failed ({e.Message}); using the Mesh API."); }
                    _gpuIndex = false;
                    gm.Index = null;
                }
            }

            GameObject go = new GameObject("g");
            go.layer = c.Key.Layer;
            go.transform.SetParent(b.Root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            MeshRenderer like = b.Like[c.Key.Material];
            mr.sharedMaterial = b.Materials[c.Key.Material];
            mr.shadowCastingMode = (ShadowCastingMode)c.Key.Shadow;
            mr.receiveShadows = c.Key.Receive;
            if (like != null) {
                mr.lightProbeUsage = like.lightProbeUsage;
                mr.reflectionProbeUsage = like.reflectionProbeUsage;
                mr.renderingLayerMask = like.renderingLayerMask;
            }
            if (c.Key.Snow > 0) {
                MaterialPropertyBlock mpb = new MaterialPropertyBlock();
                mpb.SetFloat(SnowLevel, c.Key.Snow / (float)SnowSteps);
                mr.SetPropertyBlock(mpb);
            }
            if (c.Key.Class == 0) { b.Near.Add(mr); } else if (c.Key.Class == 1) { b.Far.Add(mr); }

            MeshCount++;
            VertexCount += c.VertexCount;
            return gm;
        }

        // The cell's old meshes go and the new ones come in, in one frame. A piece is drawn by the new
        // meshes only if the build was made from its current generation; any other piece the build
        // contains has its ranges zeroed before the cell is shown, and is drawn by its own renderers.
        private void Swap(PieceBuild b) {
            PieceCell cell = b.Cell;
            foreach (PieceEntry e in cell.Pieces) {
                e.InMesh = false;
                e.Ranges.Clear();
            }
            DestroyCellObjects(cell);
            cell.Root = b.Root;
            cell.Meshes.AddRange(b.Meshes);
            cell.Build = null;

            int dead = 0;
            for (int m = 0; m < b.Members.Count; m++) {
                PieceEntry e = b.Members[m];
                bool current = e.Cell == cell && e.W != null && e.Gen == b.Gens[m];
                if (!current) { dead++; }
                foreach (PieceBuild.Span s in b.Spans[m]) {
                    PieceGroupMesh gm = b.Meshes[s.Chunk];
                    if (current) {
                        e.Ranges.Add(new PieceRange { Mesh = gm, Start = s.Start, Count = s.Count });
                    } else {
                        Array.Clear(gm.Orig, s.Start, s.Count);
                        Array.Clear(gm.Cur, s.Start, s.Count);
                        MarkDirty(gm);
                    }
                }
                if (current) { e.InMesh = true; }
            }

            cell.Dead = dead;
            foreach (PieceEntry e in cell.Pieces) {
                if (!e.InMesh) { Touch(cell); }
                if (e.InMesh && e.Suspended) { SetRanges(e, false); }
                SyncHidden(e);
            }
            if (cell.Pieces.Count == 0) {
                cell.Stale = true;
                cell.LastChange = float.MinValue;
            }
            cell.Root.SetActive(true);
        }

        private void Discard(PieceBuild b) {
            if (b.Cell.Build == b) { b.Cell.Build = null; }
            for (int i = b.Applied; i < b.Chunks.Count; i++) { VertexPool.Return(b.Chunks[i].Vertices); }
            foreach (PieceGroupMesh gm in b.Meshes) { Release(gm); }
            if (b.Root != null) { Object.Destroy(b.Root); }
        }

        private void DestroyCellObjects(PieceCell cell) {
            foreach (PieceGroupMesh gm in cell.Meshes) { Release(gm); }
            cell.Meshes.Clear();
            if (cell.Root != null) { Object.Destroy(cell.Root); }
            cell.Root = null;
        }

        private void Release(PieceGroupMesh gm) {
            gm.Index?.Dispose();
            gm.Index = null;
            if (gm.Mesh != null) {
                MeshCount--;
                VertexCount -= gm.Mesh.vertexCount;
                Object.Destroy(gm.Mesh);
            }
            gm.Mesh = null;
        }

        /// <summary>Every merged object destroyed; pieces are the caller's to restore.</summary>
        internal void DestroyAll() {
            foreach (PieceBuild b in _building) {
                if (b.Done) { Discard(b); } else { b.Cell.Build = null; }
            }
            _building.Clear();
            foreach (PieceCell cell in _cells.Values) { DestroyCellObjects(cell); }
            _cells.Clear();
            _dirty.Clear();
            if (_root != null) { Object.Destroy(_root); }
            _root = null;
        }

        // Vertex arrays in power-of-two sizes, shared by the workers that fill them and the main thread that
        // uploads them, so a rebuild does not leave megabytes of garbage behind.
        private static class VertexPool {
            private const int KeepPerSize = 4;
            private static readonly Stack<PackedVertex[]>[] Free = new Stack<PackedVertex[]>[17];

            internal static PackedVertex[] Take(int count) {
                int bucket = Bucket(count);
                lock (Free) {
                    Stack<PackedVertex[]> s = Free[bucket];
                    if (s != null && s.Count > 0) { return s.Pop(); }
                }
                return new PackedVertex[1 << bucket];
            }

            internal static void Return(PackedVertex[] a) {
                if (a == null) { return; }
                int bucket = Bucket(a.Length);
                if ((1 << bucket) != a.Length) { return; }
                lock (Free) {
                    Stack<PackedVertex[]> s = Free[bucket] ??= new Stack<PackedVertex[]>();
                    if (s.Count < KeepPerSize) { s.Push(a); }
                }
            }

            private static int Bucket(int count) {
                int b = 8;
                while ((1 << b) < count) { b++; }
                return b;
            }
        }

        internal sealed class PieceBuild {
            internal struct Key : IEquatable<Key> {
                public int Material, Layer, Shadow, Class, Snow;
                public bool Receive;

                public bool Equals(Key o) => Material == o.Material && Layer == o.Layer && Shadow == o.Shadow &&
                                             Class == o.Class && Snow == o.Snow && Receive == o.Receive;
                public override bool Equals(object o) => o is Key k && Equals(k);
                public override int GetHashCode() =>
                    ((((Material * 31 + Layer) * 31 + Shadow) * 31 + Class) * 31 + Snow) * 2 + (Receive ? 1 : 0);
            }

            internal struct Item {
                public int Member;
                public PieceMeshData Data;
                public int Submesh;
                public Matrix4x4 Matrix;
                public Vector2 UVShift;
            }

            internal struct Span {
                public int Chunk, Start, Count;
            }

            internal sealed class Chunk {
                public Key Key;
                // Pooled and longer than VertexCount; back to the pool once the mesh has copied it.
                public PackedVertex[] Vertices;
                public int VertexCount;
                public ushort[] Indices;
                public Bounds Bounds;
            }

            public PieceCell Cell;
            public float SwitchPerSize, CullPerSize;
            public readonly List<PieceEntry> Members = new List<PieceEntry>();
            public readonly List<int> Gens = new List<int>();
            public readonly List<Material> Materials = new List<Material>();
            public readonly Dictionary<int, MeshRenderer> Like = new Dictionary<int, MeshRenderer>();
            private readonly Dictionary<Material, int> _materialIndex = new Dictionary<Material, int>();
            private readonly Dictionary<Key, List<Item>> _groups = new Dictionary<Key, List<Item>>();

            // Worker output, read by the main thread after Done.
            public readonly List<Chunk> Chunks = new List<Chunk>();
            public List<Span>[] Spans;
            public volatile bool Done;
            public Exception Error;

            // Main thread, while applying.
            public int Applied;
            public GameObject Root;
            public readonly List<PieceGroupMesh> Meshes = new List<PieceGroupMesh>();
            public readonly List<Renderer> Near = new List<Renderer>();
            public readonly List<Renderer> Far = new List<Renderer>();

            public int MaterialIndex(Material m) {
                if (!_materialIndex.TryGetValue(m, out int i)) {
                    i = Materials.Count;
                    Materials.Add(m);
                    _materialIndex[m] = i;
                }
                return i;
            }

            public void Add(Key key, Item item) {
                if (!_groups.TryGetValue(key, out List<Item> items)) {
                    items = new List<Item>();
                    _groups[key] = items;
                }
                items.Add(item);
            }

            public void Run() {
                try {
                    Spans = new List<Span>[Members.Count];
                    for (int i = 0; i < Spans.Length; i++) { Spans[i] = new List<Span>(); }
                    foreach (KeyValuePair<Key, List<Item>> g in _groups) { Merge(g.Key, g.Value); }
                } catch (Exception e) {
                    Error = e;
                } finally {
                    Done = true;
                }
            }

            // Per worker thread, reused by every build that thread runs.
            [ThreadStatic] private static PackedVertex[] t_verts;
            [ThreadStatic] private static List<ushort> t_idx;
            [ThreadStatic] private static Dictionary<int, int> t_remap;

            private void Merge(Key key, List<Item> items) {
                PackedVertex[] verts = t_verts ??= new PackedVertex[MaxVertices];
                List<ushort> idx = t_idx ??= new List<ushort>();
                Dictionary<int, int> remap = t_remap ??= new Dictionary<int, int>();
                idx.Clear();
                int nv = 0;
                Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;

                foreach (Item it in items) {
                    int[] tris = it.Data.Tris[it.Submesh];
                    remap.Clear();
                    foreach (int v in tris) { remap[v] = 0; }
                    if (nv + remap.Count > MaxVertices && nv > 0) {
                        Emit(key, verts, nv, idx, min, max);
                        nv = 0;
                        min = Vector3.positiveInfinity;
                        max = Vector3.negativeInfinity;
                    }

                    Matrix4x4 m = it.Matrix;
                    Matrix4x4 nm = m.inverse.transpose;
                    remap.Clear();
                    int start = idx.Count;
                    foreach (int v in tris) {
                        if (!remap.TryGetValue(v, out int at)) {
                            at = nv++;
                            remap[v] = at;
                            Vector3 p = m.MultiplyPoint3x4(it.Data.Pos[v]);
                            min = Vector3.Min(min, p);
                            max = Vector3.Max(max, p);
                            Vector3 n = nm.MultiplyVector(it.Data.Nrm[v]).normalized;
                            Vector4 t = it.Data.Tan[v];
                            Vector3 tt = m.MultiplyVector(t).normalized;
                            verts[at] = new PackedVertex {
                                Pos = p,
                                Nx = Pack(n.x), Ny = Pack(n.y), Nz = Pack(n.z),
                                Tx = Pack(tt.x), Ty = Pack(tt.y), Tz = Pack(tt.z), Tw = Pack(t.w < 0f ? -1f : 1f),
                                UV = it.Data.UV[v] + it.UVShift,
                            };
                        }
                        idx.Add((ushort)at);
                    }
                    Spans[it.Member].Add(new Span { Chunk = Chunks.Count, Start = start, Count = idx.Count - start });
                }
                if (nv > 0) { Emit(key, verts, nv, idx, min, max); }
            }

            private void Emit(Key key, PackedVertex[] verts, int count, List<ushort> idx, Vector3 min, Vector3 max) {
                Bounds bounds = new Bounds();
                bounds.SetMinMax(min, max);
                PackedVertex[] copy = VertexPool.Take(count);
                Array.Copy(verts, copy, count);
                Chunks.Add(new Chunk { Key = key, Vertices = copy, VertexCount = count, Indices = idx.ToArray(), Bounds = bounds });
                idx.Clear();
            }

            private static short Pack(float v) => (short)Mathf.Clamp(Mathf.RoundToInt(v * 32767f), -32767, 32767);
        }
    }
}
