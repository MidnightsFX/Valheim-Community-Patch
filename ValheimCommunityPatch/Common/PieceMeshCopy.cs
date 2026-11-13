using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>A piece mesh's positions, normals, tangents, uv0 and per-submesh triangles, readable on any thread.</summary>
    internal sealed class PieceMeshData {
        public Vector3[] Pos;
        public Vector3[] Nrm;
        public Vector4[] Tan;
        public Vector2[] UV;
        public int[][] Tris;
    }

    /// <summary>
    /// The vertex data Fix Building Renderer Overhead merges, read once per mesh and kept for the session.
    /// </summary>
    /// <remarks>
    /// Most shipped piece meshes are not CPU-readable, so those are read back from their GPU buffers
    /// with AsyncGPUReadback, two at a time, and decoded from the mesh's own vertex layout. A mesh that
    /// cannot be read is remembered as failed and its pieces stay the game's.
    /// </remarks>
    internal static class PieceMeshCopy {
        private const int InFlight = 2;

        private sealed class Pending {
            public Mesh Mesh;
            public GraphicsBuffer[] Buffers;
            public AsyncGPUReadbackRequest[] Requests;
        }

        private static readonly Dictionary<Mesh, PieceMeshData> Ready = new Dictionary<Mesh, PieceMeshData>();
        private static readonly HashSet<Mesh> Failed = new HashSet<Mesh>();
        private static readonly Queue<Mesh> Waiting = new Queue<Mesh>();
        // Waiting or being read, so a mesh asked for again meanwhile is not read twice.
        private static readonly HashSet<Mesh> Requested = new HashSet<Mesh>();
        private static readonly List<Pending> InProgress = new List<Pending>();

        internal static int GpuReads { get; private set; }
        internal static int Failures => Failed.Count;

        /// <summary>The mesh's data, or null while it is being read; <paramref name="failed"/> when it cannot be.</summary>
        internal static PieceMeshData Get(Mesh mesh, out bool failed) {
            failed = Failed.Contains(mesh);
            if (failed) { return null; }
            if (Ready.TryGetValue(mesh, out PieceMeshData data)) { return data; }

            if (mesh.isReadable) {
                data = TryRead(mesh, FromCpu);
                failed = data == null;
                return data;
            }

            if (Requested.Add(mesh)) { Waiting.Enqueue(mesh); }
            return null;
        }

        /// <summary>Collects finished readbacks and starts new ones. Main thread.</summary>
        internal static void Pump() {
            for (int i = InProgress.Count - 1; i >= 0; i--) {
                Pending p = InProgress[i];
                bool done = true, error = false;
                foreach (AsyncGPUReadbackRequest r in p.Requests) {
                    if (!r.done) { done = false; break; }
                    if (r.hasError) { error = true; }
                }
                if (!done) { continue; }

                InProgress.RemoveAt(i);
                if (error || p.Mesh == null) {
                    Fail(p.Mesh, "the GPU readback failed");
                } else {
                    byte[][] streams = new byte[p.Requests.Length - 1][];
                    for (int s = 0; s < streams.Length; s++) { streams[s] = p.Requests[s].GetData<byte>().ToArray(); }
                    byte[] index = p.Requests[p.Requests.Length - 1].GetData<byte>().ToArray();
                    TryRead(p.Mesh, m => FromBytes(m, streams, index));
                }
                foreach (GraphicsBuffer b in p.Buffers) { b?.Dispose(); }
                Requested.Remove(p.Mesh);
            }

            while (InProgress.Count < InFlight && Waiting.Count > 0) {
                Mesh mesh = Waiting.Dequeue();
                if (mesh == null || Ready.ContainsKey(mesh) || Failed.Contains(mesh) || !Start(mesh)) { Requested.Remove(mesh); }
            }
        }

        internal static void Clear() {
            foreach (Pending p in InProgress) {
                foreach (GraphicsBuffer b in p.Buffers) { b?.Dispose(); }
            }
            InProgress.Clear();
            Waiting.Clear();
            Requested.Clear();
            Ready.Clear();
            Failed.Clear();
        }

        private static bool Start(Mesh mesh) {
            Pending p = new Pending { Mesh = mesh, Buffers = new GraphicsBuffer[mesh.vertexBufferCount + 1] };
            p.Requests = new AsyncGPUReadbackRequest[p.Buffers.Length];
            try {
                for (int s = 0; s < mesh.vertexBufferCount; s++) {
                    p.Buffers[s] = mesh.GetVertexBuffer(s);
                    p.Requests[s] = AsyncGPUReadback.Request(p.Buffers[s]);
                }
                p.Buffers[mesh.vertexBufferCount] = mesh.GetIndexBuffer();
                p.Requests[mesh.vertexBufferCount] = AsyncGPUReadback.Request(p.Buffers[mesh.vertexBufferCount]);
                GpuReads++;
                InProgress.Add(p);
                return true;
            } catch (Exception e) {
                foreach (GraphicsBuffer b in p.Buffers) { b?.Dispose(); }
                Fail(mesh, e.Message);
                return false;
            }
        }

        private static PieceMeshData TryRead(Mesh mesh, Func<Mesh, PieceMeshData> read) {
            try {
                PieceMeshData data = read(mesh);
                Ready[mesh] = data;
                return data;
            } catch (Exception e) {
                Fail(mesh, e.Message);
                return null;
            }
        }

        private static void Fail(Mesh mesh, string why) {
            if (mesh == null) { return; }
            Failed.Add(mesh);
            Logger.LogDebug($"Fix Building Renderer Overhead: mesh '{mesh.name}' could not be read ({why}); its pieces stay as they are.");
        }

        private static PieceMeshData FromCpu(Mesh m) {
            int n = m.vertexCount;
            PieceMeshData d = new PieceMeshData { Pos = m.vertices, Nrm = m.normals, Tan = m.tangents };
            List<Vector2> uv = new List<Vector2>(n);
            m.GetUVs(0, uv);
            d.UV = uv.Count == n ? uv.ToArray() : new Vector2[n];
            if (d.Nrm.Length != n) { d.Nrm = new Vector3[n]; }
            if (d.Tan.Length != n) { d.Tan = new Vector4[n]; }
            d.Tris = new int[m.subMeshCount][];
            for (int s = 0; s < m.subMeshCount; s++) {
                if (m.GetTopology(s) != MeshTopology.Triangles) { throw new InvalidOperationException("not triangles"); }
                d.Tris[s] = m.GetTriangles(s, true);
            }
            return d;
        }

        private static PieceMeshData FromBytes(Mesh m, byte[][] streams, byte[] index) {
            int n = m.vertexCount;
            PieceMeshData d = new PieceMeshData { Pos = new Vector3[n], Nrm = new Vector3[n], Tan = new Vector4[n], UV = new Vector2[n] };
            foreach (VertexAttributeDescriptor a in m.GetVertexAttributes()) {
                if (a.attribute != VertexAttribute.Position && a.attribute != VertexAttribute.Normal &&
                    a.attribute != VertexAttribute.Tangent && a.attribute != VertexAttribute.TexCoord0) { continue; }
                int stride = m.GetVertexBufferStride(a.stream);
                int offset = m.GetVertexAttributeOffset(a.attribute);
                byte[] buf = streams[a.stream];
                for (int v = 0; v < n; v++) {
                    Vector4 x = Read(buf, v * stride + offset, a.format, a.dimension);
                    switch (a.attribute) {
                        case VertexAttribute.Position: d.Pos[v] = x; break;
                        case VertexAttribute.Normal: d.Nrm[v] = x; break;
                        case VertexAttribute.Tangent: d.Tan[v] = x; break;
                        default: d.UV[v] = x; break;
                    }
                }
            }

            bool wide = m.indexFormat == IndexFormat.UInt32;
            d.Tris = new int[m.subMeshCount][];
            for (int s = 0; s < m.subMeshCount; s++) {
                SubMeshDescriptor sub = m.GetSubMesh(s);
                if (sub.topology != MeshTopology.Triangles) { throw new InvalidOperationException("not triangles"); }
                int[] t = new int[sub.indexCount];
                for (int i = 0; i < t.Length; i++) {
                    int at = sub.indexStart + i;
                    t[i] = (wide ? (int)BitConverter.ToUInt32(index, at * 4) : BitConverter.ToUInt16(index, at * 2)) + sub.baseVertex;
                }
                d.Tris[s] = t;
            }
            return d;
        }

        private static Vector4 Read(byte[] b, int at, VertexAttributeFormat f, int dimension) {
            Vector4 r = Vector4.zero;
            for (int i = 0; i < Mathf.Min(dimension, 4); i++) {
                switch (f) {
                    case VertexAttributeFormat.Float32: r[i] = BitConverter.ToSingle(b, at + i * 4); break;
                    case VertexAttributeFormat.Float16: r[i] = Mathf.HalfToFloat(BitConverter.ToUInt16(b, at + i * 2)); break;
                    case VertexAttributeFormat.UNorm8: r[i] = b[at + i] / 255f; break;
                    case VertexAttributeFormat.SNorm8: r[i] = Mathf.Max((sbyte)b[at + i] / 127f, -1f); break;
                    case VertexAttributeFormat.UNorm16: r[i] = BitConverter.ToUInt16(b, at + i * 2) / 65535f; break;
                    case VertexAttributeFormat.SNorm16: r[i] = Mathf.Max(BitConverter.ToInt16(b, at + i * 2) / 32767f, -1f); break;
                    default: throw new InvalidOperationException("vertex format " + f);
                }
            }
            return r;
        }
    }
}
