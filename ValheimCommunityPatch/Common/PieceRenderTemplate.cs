using System;
using System.Collections.Generic;
using UnityEngine;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>A LODGroup of a piece, as Fix Building Renderer Overhead needs it to choose a level itself.</summary>
    internal sealed class PieceLodGroup {
        public LODGroup Group;
        public Vector3 WorldRef;
        public float WorldSize;
        public float[] Heights;
        // Every renderer in its levels is drawn by the merge, so it can be switched off while they are.
        public bool Owned;
    }

    /// <summary>One submesh of one renderer of a piece, drawn by a merged mesh instead of by its renderer.</summary>
    internal sealed class PiecePart {
        public MeshRenderer Renderer;
        public Mesh Mesh;
        public int Submesh;
        public Material Material;
        public Matrix4x4 LocalToWorld;
        // Null when the renderer is in no level of a LODGroup, which Unity then never culls by distance.
        public PieceLodGroup Lod;
        public int Level;
    }

    /// <summary>
    /// Which renderers of a placed piece Fix Building Renderer Overhead may draw in a merged mesh.
    /// </summary>
    /// <remarks>
    /// A piece is taken only when every script on it is one that never changes how it is drawn after
    /// it is placed; anything else (doors, lids, fires, stands, signs) stays the game's. Its wet and
    /// snow overlays are left to the game too, since they switch on and off by themselves. Only the
    /// Custom/Piece shader is merged: it reads nothing per object except the instanced Props values,
    /// which the fix carries, and an albedo tint hashed from the object's translation, which a merged
    /// mesh cannot reproduce, so materials that use it per object stay the game's.
    /// </remarks>
    internal static class PieceRenderTemplate {
        private const string PieceShader = "Custom/Piece";

        // RandomPieceRotation turns the piece once in Awake. SimpleMeshCombine is an editor tool left on some
        // prefabs (wood walls among them) that never runs in the game.
        private static readonly HashSet<Type> AllowedScripts = new HashSet<Type> {
            typeof(WearNTear), typeof(Piece), typeof(ZNetView), typeof(RandomMaterialValues),
            typeof(RandomPieceRotation), typeof(DropOnDestroyed), typeof(EffectArea), typeof(HoverText),
            typeof(LodFadeInOut), typeof(MaterialManNotifier), typeof(SimpleMeshCombine),
        };

        private static readonly int ValueNoise = Shader.PropertyToID("_ValueNoise");
        private static readonly int ValueNoiseVertex = Shader.PropertyToID("_ValueNoiseVertex");

        private static readonly Dictionary<int, string> PrefabVerdict = new Dictionary<int, string>();
        private static readonly Dictionary<Material, string> MaterialVerdict = new Dictionary<Material, string>();
        private static readonly List<MonoBehaviour> ScriptBuffer = new List<MonoBehaviour>();
        private static readonly List<MeshRenderer> Renderers = new List<MeshRenderer>();
        private static readonly List<Material> Materials = new List<Material>();

        internal static void Clear() {
            PrefabVerdict.Clear();
            MaterialVerdict.Clear();
        }

        /// <summary>
        /// Fills <paramref name="parts"/>, <paramref name="renderers"/> and <paramref name="groups"/> with what
        /// the piece draws, or returns why it stays the game's.
        /// </summary>
        internal static string Collect(WearNTear w, List<PiecePart> parts, List<MeshRenderer> renderers, List<PieceLodGroup> groups) {
            parts.Clear();
            renderers.Clear();
            groups.Clear();

            ZNetView nview = w.m_nview;
            if (nview == null || nview.m_ghost || !nview.IsValid()) { return "not placed"; }

            int prefab = nview.GetZDO().GetPrefab();
            if (!PrefabVerdict.TryGetValue(prefab, out string verdict)) {
                verdict = Scripts(w);
                PrefabVerdict[prefab] = verdict;
            }
            if (verdict != null) { return verdict; }
            if (w.GetComponentInParent<Rigidbody>() != null) { return "moves"; }

            Transform wet = w.m_wet != null ? w.m_wet.transform : null;
            w.GetComponentsInChildren(false, Renderers);
            foreach (MeshRenderer r in Renderers) {
                if (!r.enabled || r.forceRenderingOff) { continue; }
                if (r == w.m_snow || r == w.m_snowWorn || r == w.m_snowBroken) { continue; }
                if (wet != null && r.transform.IsChildOf(wet)) { continue; }

                MeshFilter filter = r.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null) { return "renderer without a mesh"; }
                if (mesh.vertexCount > PieceMerge.MaxVertices) { return "mesh too large"; }
                if (r.additionalVertexStreams != null) { return "extra vertex streams"; }

                Matrix4x4 l2w = r.localToWorldMatrix;
                if (l2w.determinant <= 0f) { return "mirrored"; }

                r.GetSharedMaterials(Materials);
                if (Materials.Count == 0 || Materials.Count > mesh.subMeshCount) { return "material count"; }

                PieceLodGroup lod = null;
                int level = 0;
                LODGroup g = r.GetComponentInParent<LODGroup>();
                if (g != null) {
                    if (g.fadeMode == LODFadeMode.CrossFade) { return "cross-faded LOD"; }
                    lod = LodOf(g, r, groups, out level);
                }

                for (int s = 0; s < Materials.Count; s++) {
                    Material m = Materials[s];
                    string why = MaterialBlocker(m);
                    if (why != null) { return why; }
                    parts.Add(new PiecePart {
                        Renderer = r, Mesh = mesh, Submesh = s, Material = m, LocalToWorld = l2w, Lod = lod, Level = level,
                    });
                }
                renderers.Add(r);
            }

            if (parts.Count == 0) { return "nothing drawn"; }

            foreach (PieceLodGroup g in groups) {
                g.Owned = true;
                foreach (LOD l in g.Group.GetLODs()) {
                    foreach (Renderer r in l.renderers) {
                        if (r != null && !(r is MeshRenderer mr && renderers.Contains(mr))) { g.Owned = false; }
                    }
                }
            }
            return null;
        }

        private static string Scripts(WearNTear w) {
            w.GetComponentsInChildren(true, ScriptBuffer);
            foreach (MonoBehaviour mb in ScriptBuffer) {
                if (mb != null && !AllowedScripts.Contains(mb.GetType())) { return "script " + mb.GetType().Name; }
            }
            return null;
        }

        private static string MaterialBlocker(Material m) {
            if (m == null) { return "missing material"; }
            if (MaterialVerdict.TryGetValue(m, out string v)) { return v; }

            if (m.shader == null || m.shader.name != PieceShader) {
                v = "shader " + (m.shader != null ? m.shader.name : "missing");
            } else if (m.renderQueue >= 2500) {
                v = "transparent";
            } else if (m.HasProperty(ValueNoise) && m.GetFloat(ValueNoise) > 0f &&
                       (!m.HasProperty(ValueNoiseVertex) || m.GetFloat(ValueNoiseVertex) < 0.5f)) {
                v = "per-object tint";
            }

            MaterialVerdict[m] = v;
            return v;
        }

        // Renderers under a LODGroup but in none of its levels are drawn at every distance by Unity.
        private static PieceLodGroup LodOf(LODGroup g, Renderer r, List<PieceLodGroup> groups, out int level) {
            LOD[] lods = g.GetLODs();
            level = -1;
            for (int k = 0; k < lods.Length && level < 0; k++) {
                if (Array.IndexOf(lods[k].renderers, r) >= 0) { level = k; }
            }
            if (level < 0) {
                level = 0;
                return null;
            }

            foreach (PieceLodGroup known in groups) {
                if (known.Group == g) { return known; }
            }

            Vector3 s = g.transform.lossyScale;
            PieceLodGroup info = new PieceLodGroup {
                Group = g,
                WorldRef = g.transform.TransformPoint(g.localReferencePoint),
                WorldSize = g.size * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z))),
                Heights = Array.ConvertAll(lods, l => l.screenRelativeTransitionHeight),
            };
            groups.Add(info);
            return info;
        }

        /// <summary>
        /// The level Unity's LODGroup would show: the first whose transition height the group's screen height
        /// still reaches, with distance divided by lodBias and levels below maximumLODLevel skipped; past the
        /// last one the group is culled (returns the level count).
        /// </summary>
        internal static int ChooseLevel(PieceLodGroup lod, Vector3 camera, float halfTan, float lodBias, int maxLevel) {
            float d = Vector3.Distance(lod.WorldRef, camera) / lodBias;
            float relative = d <= 0.0001f ? float.MaxValue : lod.WorldSize * 0.5f / (d * halfTan);
            for (int k = maxLevel; k < lod.Heights.Length; k++) {
                if (relative >= lod.Heights[k]) { return k; }
            }
            return lod.Heights.Length;
        }
    }
}
