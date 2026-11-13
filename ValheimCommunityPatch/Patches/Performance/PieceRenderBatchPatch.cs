using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Building Renderer Overhead: the plain building pieces of each 16 m area are drawn as a few merged
    // meshes instead of one renderer each. On by default; each player can switch it off (Client config,
    // Building Render Batching), the one performance fix with a switch, because it changes how pieces are drawn.
    //
    // Unity does per-renderer work every frame for every piece in range: culling, the shadow-caster setup
    // for each shadow cascade and draw submission, all on the main thread. A big base has tens of
    // thousands of piece renderers, and that work, not the GPU, sets its frame rate.
    //
    // A WearNTear.Awake postfix queues each placed piece; half a second later it is adopted if every
    // script on it is one that never changes how it is drawn and its materials use the piece shader
    // without a per-object tint (Common/PieceRenderTemplate). Its renderers' meshes are merged per cell
    // on a worker thread (Common/PieceMerge) and swapped in whole; only then are its own renderers
    // hidden with forceRenderingOff, and the LODGroups whose every renderer is merged switched off, since
    // Unity keeps evaluating them for hidden renderers. The piece itself is untouched:
    // colliders, scripts and network state stay live. Anything that changes how it looks puts it back:
    // a health-stage change re-collects it, MaterialMan values are read in a PropertyContainer.UpdateBlock
    // postfix (hover highlight and Ashlands glow take it out of its merged mesh for as long as they last,
    // snow and Grausten's texture offset go into the mesh, anything else gives it back to the game), and
    // a destroyed piece is zeroed out of its mesh at once. Destruction debris takes only renderers the
    // camera saw last frame, so the fragments call in RPC_CreateFragments is redirected to a copy that
    // counts a hidden renderer as seen when its merged copy was drawn. Stands down next to another mod
    // that hides or merges piece renderers, or that hooks a method it reads or reproduces.
    //
    // Client: rendering.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(WearNTear))]
    internal static class PieceRenderBatchPatch {
        private const string FixName = "Fix Building Renderer Overhead";
        // 16 m drew as fast as 32 m on a 27,000-piece castle with a third fewer triangles: smaller cells
        // are culled against the view and the shadow cascades more closely.
        private const float CellSize = 16f;
        private const float AdoptAfter = 0.5f;
        private const float RetryTransient = 1f;
        private const double BudgetMs = 1.0;
        private const double LoadingBudgetMs = 8.0;
        private const int SweepPerFrame = 40;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Enabled = ValConfig.cfg.Bind(
                "Client config",
                "Building Render Batching",
                true,
                new ConfigDescription(
                    "Draws the plain building pieces of each 16 m area as a few merged meshes instead of one " +
                    "object each, which makes big bases much faster to draw. Pieces stay real pieces, and any " +
                    "piece whose look changes (highlighted, damaged, burning, destroyed) is drawn the game's way " +
                    "again. Switch it off to draw every piece the game's way. Can be changed while playing."));
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Building Render Batching",
                false,
                "Diagnostic. Checks a sample of merged pieces every second against their real state and logs " +
                "any difference, plus a summary every 30 s (pieces merged, reasons pieces were left alone, " +
                "cells, meshes, main-thread time). Leave it off unless you are validating the fix.",
                advanced: true);
        }

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(PieceRenderBatchPatch));

        private static readonly MethodInfo UpdateBlockMethod =
            AccessTools.DeclaredMethod(typeof(MaterialMan.PropertyContainer), "UpdateBlock");
        private static readonly MethodInfo CreateFragmentsMethod =
            AccessTools.DeclaredMethod(typeof(Destructible), nameof(Destructible.CreateFragments), new[] { typeof(GameObject), typeof(bool) });

        private static readonly HookHealth Hooks = new HookHealth(
            typeof(PieceRenderBatchPatch),
            FixName,
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(WearNTear), "Awake"), typeof(PieceRenderBatchPatch))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(WearNTear), "SetHealthVisual"), typeof(PieceRenderBatchPatch))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(WearNTear), "OnDestroy"), typeof(TeardownHooks.PieceHook))
               && PatchHelper.HasHook(UpdateBlockMethod, typeof(BlockHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(MonoUpdaters), "LateUpdate"), typeof(DriverHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZNetScene), nameof(ZNetScene.Shutdown)), typeof(ShutdownHook))
               && FragmentsHook.Matched);

        // A skipped or rewritten UpdateBlock would leave the shared block this fix reads stale, and the
        // debris copy would not run another mod's hooks on CreateFragments.
        private static readonly TakeoverCheck[] Takeovers = {
            new TakeoverCheck(typeof(PieceRenderBatchPatch), UpdateBlockMethod, HookKinds.BoolPrefixes | HookKinds.Transpilers,
                owners => $"MaterialMan's property blocks are changed by {owners}, so '{FixName}' stands down."),
            new TakeoverCheck(typeof(PieceRenderBatchPatch), CreateFragmentsMethod, HookKinds.Any,
                owners => $"Destruction debris is changed by {owners}, so '{FixName}' stands down."),
        };

        private static bool _decided;
        private static bool _engaged;
        private static bool _active;

        private static PieceMerge _merge;
        private static readonly Dictionary<int, PieceEntry> ById = new Dictionary<int, PieceEntry>();
        private static readonly Dictionary<int, PieceEntry> ByRenderer = new Dictionary<int, PieceEntry>();
        private static readonly List<PieceEntry> All = new List<PieceEntry>();
        private static readonly Queue<KeyValuePair<WearNTear, float>> Queue = new Queue<KeyValuePair<WearNTear, float>>();
        private static readonly HashSet<int> Queued = new HashSet<int>();
        private static readonly HashSet<int> Released = new HashSet<int>();
        private static readonly Dictionary<string, int> Rejects = new Dictionary<string, int>();
        private static int _sweep;
        private static int _adopted;

        private static bool Decide() {
            if (RunMode.IsHeadless || !Hooks.Healthy) { return false; }

            bool takenOver = false;
            foreach (TakeoverCheck check in Takeovers) { takenOver |= check.TakenOver; }
            if (takenOver) { return false; }

            string other = BuildingMergeCompat.Other();
            if (other != null) {
                FixRegistry.MarkStoodDown(typeof(PieceRenderBatchPatch));
                Logger.LogInfo($"{other} draws building pieces its own way, so '{FixName}' stands down.");
                return false;
            }
            return true;
        }

        private static void Begin() {
            _active = true;
            _merge = new PieceMerge(CellSize);
            float now = Time.time - AdoptAfter;
            foreach (WearNTear w in WearNTear.s_allInstances) { Enqueue(w, now); }
            Logger.LogDebug($"{FixName}: on, {Queue.Count} pieces queued.");
        }

        // Every piece back to the game's own drawing and every merged object gone.
        private static void End() {
            foreach (PieceEntry e in All) {
                e.InMesh = false;
                e.Suspended = false;
                PieceMerge.SyncHidden(e);
            }
            _merge?.DestroyAll();
            _merge = null;
            ById.Clear();
            ByRenderer.Clear();
            All.Clear();
            Queue.Clear();
            Queued.Clear();
            Released.Clear();
            _sweep = 0;
            _active = false;
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Shutdown))]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() {
                if (_active) { End(); }
                PieceMeshCopy.Clear();
                PieceRenderTemplate.Clear();
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch("Awake")]
        private static void AwakePostfix(WearNTear __instance) {
            if (_active) { Enqueue(__instance, Time.time); }
        }

        private static void Enqueue(WearNTear w, float at) {
            if (w == null) { return; }
            int id = w.GetInstanceID();
            if (ById.ContainsKey(id) || Released.Contains(id) || !Queued.Add(id)) { return; }
            Queue.Enqueue(new KeyValuePair<WearNTear, float>(w, at));
        }

        private static void Adopt(Func<bool> left) {
            float now = Time.time;
            int done = 0;
            while (Queue.Count > 0 && (done == 0 || left())) {
                KeyValuePair<WearNTear, float> next = Queue.Peek();
                if (now - next.Value < AdoptAfter) { return; }
                Queue.Dequeue();
                WearNTear w = next.Key;
                if (w == null) { continue; }
                int id = w.GetInstanceID();
                Queued.Remove(id);
                if (ById.ContainsKey(id) || Released.Contains(id)) { continue; }
                done++;
                Adopt(w, id);
            }
        }

        private static void Adopt(WearNTear w, int id) {
            PieceEntry e = new PieceEntry { W = w, Id = id };
            string why = Collect(e, out bool retry);
            if (retry) {
                Queued.Add(id);
                Queue.Enqueue(new KeyValuePair<WearNTear, float>(w, Time.time + RetryTransient - AdoptAfter));
                return;
            }
            if (why != null) {
                Count(why);
                Released.Add(id);
                return;
            }

            ById[id] = e;
            e.Index = All.Count;
            All.Add(e);
            Register(e);
            _adopted++;
        }

        // Fills the entry and puts it in its cell; null when it is taken, else why not.
        private static string Collect(PieceEntry e, out bool retry) {
            retry = false;
            string why = PieceRenderTemplate.Collect(e.W, e.Parts, e.Renderers, e.Groups);
            if (why != null) { return why; }

            Props props = ReadProps(e.W);
            if (props.Unknown) { return "material values"; }
            if (props.Transient) {
                retry = true;
                return "highlighted";
            }
            foreach (PiecePart p in e.Parts) {
                PieceMeshCopy.Get(p.Mesh, out bool failed);
                if (failed) { return "unreadable mesh"; }
            }

            e.UVOffset = props.UV;
            e.SnowBucket = PieceMerge.SnowStep(props.Snow);
            e.Stage = Stage(e.W);
            e.Cell = _merge.CellFor(e.W.transform.position);
            e.Cell.Pieces.Add(e);
            PieceMerge.Touch(e.Cell);
            return null;
        }

        private static void Register(PieceEntry e) {
            foreach (MeshRenderer r in e.Renderers) { ByRenderer[r.GetInstanceID()] = e; }
        }

        private static void UnregisterRenderers(PieceEntry e) {
            foreach (MeshRenderer r in e.Renderers) {
                if (r != null) { ByRenderer.Remove(r.GetInstanceID()); }
            }
        }

        private static void Forget(PieceEntry e) {
            UnregisterRenderers(e);
            ById.Remove(e.Id);
            int last = All.Count - 1;
            PieceEntry moved = All[last];
            All[e.Index] = moved;
            moved.Index = e.Index;
            All.RemoveAt(last);
        }

        // For good: the piece is drawn the game's way until it is destroyed.
        private static void Release(PieceEntry e, string why) {
            Count(why);
            _merge.Detach(e);
            if (e.W != null) { Released.Add(e.Id); }
            Forget(e);
        }

        // Its renderers changed (another health stage): out of its mesh now, back in at the next build.
        private static void Recollect(PieceEntry e) {
            _merge.Detach(e);
            UnregisterRenderers(e);
            string why = Collect(e, out bool retry);
            if (why == null) {
                Register(e);
                return;
            }
            Forget(e);
            if (e.W == null) { return; }
            if (retry) {
                Enqueue(e.W, Time.time + RetryTransient - AdoptAfter);
            } else {
                Count(why);
                Released.Add(e.Id);
            }
        }

        /// <summary>Called from <see cref="TeardownHooks"/> for every destroyed piece.</summary>
        internal static void OnPieceDestroyed(int id) {
            if (!_active) { return; }
            Queued.Remove(id);
            Released.Remove(id);
            if (!ById.TryGetValue(id, out PieceEntry e)) { return; }
            _merge.Detach(e);
            Forget(e);
        }

        private static void Count(string why) {
            Rejects.TryGetValue(why, out int n);
            Rejects[why] = n + 1;
        }

        private static int Stage(WearNTear w) =>
            (w.m_new != null && w.m_new.activeSelf ? 1 : 0) |
            (w.m_worn != null && w.m_worn.activeSelf ? 2 : 0) |
            (w.m_broken != null && w.m_broken.activeSelf ? 4 : 0);

        [HarmonyPostfix]
        [HarmonyPatch("SetHealthVisual")]
        private static void SetHealthVisualPostfix(WearNTear __instance) {
            if (!_active || ById.Count == 0) { return; }
            if (!ById.TryGetValue(__instance.GetInstanceID(), out PieceEntry e)) { return; }
            int stage = Stage(__instance);
            if (stage == e.Stage) { return; }
            Recollect(e);
        }

        private static readonly int UVOffset = Shader.PropertyToID("_UVOffset");
        private static readonly int SnowLevel = Shader.PropertyToID("_SnowLevel");
        private static readonly int Ash = Shader.PropertyToID("_TakingAshlandsDamage");

        private struct Props {
            public bool Unknown, Transient;
            public Vector4 UV;
            public float Snow;
        }

        private static Props ReadProps(WearNTear w) {
            MaterialMan man = MaterialMan.instance;
            if (man == null || !man.m_blocks.TryGetValue(w.gameObject.GetInstanceID(), out MaterialMan.PropertyContainer c)) { return default; }
            return ReadProps(c.m_shaderProperties);
        }

        private static Props ReadProps(Dictionary<int, MaterialMan.ShaderPropertyBase> values) {
            Props p = default;
            foreach (KeyValuePair<int, MaterialMan.ShaderPropertyBase> kv in values) {
                int id = kv.Key;
                if (id == UVOffset && kv.Value is MaterialMan.ShaderProperty<Vector4> uv) {
                    p.UV = uv.Get();
                } else if (id == SnowLevel && kv.Value is MaterialMan.ShaderProperty<float> snow) {
                    p.Snow = snow.Get();
                } else if (id == Ash && kv.Value is MaterialMan.ShaderProperty<float> ash) {
                    p.Transient |= ash.Get() > 0f;
                } else if (id == ShaderProps._Color || id == ShaderProps._EmissionColor) {
                    p.Transient = true;
                } else {
                    p.Unknown = true;
                }
            }
            return p;
        }

        // MaterialMan writes one shared block to every renderer of a container; what it holds after the
        // write is exactly what the piece now shows.
        [HarmonyPatch]
        internal static class BlockHook {
            private static MethodBase TargetMethod() => UpdateBlockMethod;

            [HarmonyPostfix]
            private static void Postfix(MaterialMan.PropertyContainer __instance) {
                if (!_active || ByRenderer.Count == 0) { return; }
                // The list includes inactive renderers, such as another health stage's, so a piece's own
                // can come anywhere in it.
                List<Renderer> renderers = __instance.m_assignedRenderers;
                PieceEntry e = null;
                for (int i = 0; i < renderers.Count && e == null; i++) {
                    Renderer r = renderers[i];
                    if (r != null) { ByRenderer.TryGetValue(r.GetInstanceID(), out e); }
                }
                if (e == null) { return; }

                Props p = ReadProps(__instance.m_shaderProperties);
                if (p.Unknown) {
                    Release(e, "material values");
                    return;
                }
                if (p.Transient) {
                    if (!e.Suspended) {
                        e.Suspended = true;
                        if (e.InMesh) { _merge.SetRanges(e, false); }
                        PieceMerge.SyncHidden(e);
                    }
                    return;
                }
                if (e.Suspended) {
                    e.Suspended = false;
                    if (e.InMesh) { _merge.SetRanges(e, true); }
                    PieceMerge.SyncHidden(e);
                }
                int snow = PieceMerge.SnowStep(p.Snow);
                if (p.UV != e.UVOffset || snow != e.SnowBucket) {
                    e.UVOffset = p.UV;
                    e.SnowBucket = snow;
                    e.Gen++;
                    if (e.Cell != null) { PieceMerge.Touch(e.Cell); }
                }
            }
        }

        [HarmonyPatch(typeof(WearNTear), "RPC_CreateFragments")]
        internal static class FragmentsHook {
            internal static bool Matched;

            [HarmonyTranspiler]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original) {
                IEnumerable<CodeInstruction> result = PatchHelper.ReplaceCalls(
                    instructions,
                    CreateFragmentsMethod,
                    AccessTools.DeclaredMethod(typeof(PieceRenderBatchPatch), nameof(CreateFragments)),
                    "WearNTear.RPC_CreateFragments",
                    expected: 2,
                    original);
                Matched = !ReferenceEquals(result, instructions);
                return result;
            }
        }

        // Destructible.CreateFragments, with a hidden renderer counted as seen when its merged copy was in
        // view at the level the merge drew it.
        private static void CreateFragments(GameObject rootObject, bool visibleOnly) {
            WearNTear w = _active && visibleOnly && rootObject != null ? rootObject.GetComponent<WearNTear>() : null;
            if (w == null || !ById.TryGetValue(w.GetInstanceID(), out PieceEntry e) || !e.Hidden) {
                Destructible.CreateFragments(rootObject, visibleOnly);
                return;
            }

            Camera cam = Utils.GetMainCamera();
            Plane[] planes = cam != null ? GeometryUtility.CalculateFrustumPlanes(cam) : null;
            int layer = LayerMask.NameToLayer("effect");
            List<Rigidbody> bodies = new List<Rigidbody>();
            foreach (MeshRenderer renderer in rootObject.GetComponentsInChildren<MeshRenderer>(includeInactive: true)) {
                if (!renderer.gameObject.activeInHierarchy || (!renderer.isVisible && !DrawnByMerge(e, renderer, cam, planes))) { continue; }
                MeshFilter filter = renderer.gameObject.GetComponent<MeshFilter>();
                if (filter == null) { continue; }
                if (filter.sharedMesh == null) {
                    ZLog.Log("Meshfilter missing mesh " + filter.gameObject.name);
                    continue;
                }
                GameObject fragment = new GameObject();
                fragment.layer = layer;
                fragment.transform.position = filter.transform.position;
                fragment.transform.rotation = filter.transform.rotation;
                fragment.transform.localScale = filter.transform.lossyScale * 0.9f;
                fragment.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                fragment.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
                MaterialMan.instance.SetValue(fragment, ShaderProps._RippleDistance, 0f);
                MaterialMan.instance.SetValue(fragment, ShaderProps._ValueNoise, 0f);
                Rigidbody body = fragment.AddComponent<Rigidbody>();
                fragment.AddComponent<BoxCollider>();
                bodies.Add(body);
                fragment.AddComponent<TimedDestruction>().Trigger(UnityEngine.Random.Range(2, 4));
            }
            if (bodies.Count == 0) { return; }
            Vector3 center = Vector3.zero;
            foreach (Rigidbody body in bodies) { center += body.worldCenterOfMass; }
            center /= bodies.Count;
            foreach (Rigidbody body in bodies) {
                Vector3 force = (body.worldCenterOfMass - center).normalized * 4f;
                force += UnityEngine.Random.onUnitSphere * 1f;
                body.AddForce(force, ForceMode.VelocityChange);
            }
        }

        private static bool DrawnByMerge(PieceEntry e, MeshRenderer r, Camera cam, Plane[] planes) {
            if (cam == null || !e.Renderers.Contains(r) || !GeometryUtility.TestPlanesAABB(planes, r.bounds)) { return false; }
            foreach (PiecePart p in e.Parts) {
                if (p.Renderer != r) { continue; }
                if (p.Lod == null) { return true; }
                float halfTan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                int level = PieceRenderTemplate.ChooseLevel(p.Lod, cam.transform.position, halfTan, QualitySettings.lodBias, QualitySettings.maximumLODLevel);
                return level == p.Level || (p.Lod.Heights.Length == 1 && level == 0);
            }
            return false;
        }

        private static double _frameMs;
        private static double _worstFrameMs;
        private static int _frames;
        private static float _verifyAt;
        private static float _summaryAt;

        [HarmonyPatch(typeof(MonoUpdaters), "LateUpdate")]
        internal static class DriverHook {
            // A finalizer, so it runs on frames where an object's late update throws, which skip postfixes.
            [HarmonyFinalizer]
            private static void Finalizer() {
                bool want = Enabled != null && Enabled.Value && !ApiSwitch.Off && ZNetScene.instance != null;
                if (want && !_decided) {
                    _decided = true;
                    _engaged = Decide();
                }
                want &= _engaged;
                if (want != _active) {
                    if (want) { Begin(); } else { End(); }
                }
                if (!_active) { return; }

                long start = Stopwatch.GetTimestamp();
                try {
                    Frame(start);
                } catch (Exception ex) {
                    Logger.LogError($"{FixName}: {ex}");
                    End();
                    _engaged = false;
                }
                double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                _frameMs += ms;
                _worstFrameMs = Math.Max(_worstFrameMs, ms);
                _frames++;
            }
        }

        private static long _deadline;
        private static readonly Func<bool> Left = () => Stopwatch.GetTimestamp() < _deadline;

        private static void Frame(long start) {
            _deadline = start + (long)((RunMode.InLoadingScreen() ? LoadingBudgetMs : BudgetMs) * Stopwatch.Frequency / 1000.0);

            PieceMeshCopy.Pump();
            foreach (PieceEntry e in _merge.Unreadable) {
                if (e.Cell != null || e.InMesh) { Release(e, "unreadable mesh"); }
            }
            _merge.Unreadable.Clear();

            Adopt(Left);
            _merge.Tick(Left);
            Sweep();
            _merge.Flush();

            if (Verify.Value) { VerifyTick(); }
        }

        // Catches what no hook sees: a renderer switched off or swapped by another mod.
        private static void Sweep() {
            for (int n = 0; n < SweepPerFrame && All.Count > 0; n++) {
                if (_sweep >= All.Count) { _sweep = 0; }
                PieceEntry e = All[_sweep++];
                if (e.W == null) {
                    _merge.Detach(e);
                    Forget(e);
                    continue;
                }
                if (!e.Hidden) { continue; }
                foreach (PiecePart p in e.Parts) {
                    MeshRenderer r = p.Renderer;
                    if (r == null || !r.gameObject.activeInHierarchy || !r.enabled) {
                        Recollect(e);
                        break;
                    }
                    if (!r.forceRenderingOff) {
                        Release(e, "shown by another mod");
                        break;
                    }
                    if (p.Submesh == 0 && r.sharedMaterial != p.Material) {
                        Recollect(e);
                        break;
                    }
                }
            }
        }

        private static readonly System.Random Sampler = new System.Random(1);

        private static void VerifyTick() {
            float now = Time.unscaledTime;
            if (now >= _verifyAt && All.Count > 0) {
                _verifyAt = now + 1f;
                for (int i = 0; i < 64; i++) {
                    PieceEntry e = All[Sampler.Next(All.Count)];
                    string diff = Check(e);
                    if (diff != null) { Logger.LogWarning($"{FixName} verify: {(e.W != null ? e.W.name : "?")}: {diff}"); }
                }
            }
            if (now >= _summaryAt) {
                _summaryAt = now + 30f;
                Logger.LogInfo(
                    $"{FixName}: {All.Count} pieces taken ({_adopted} adopted in all), {Queue.Count} queued, " +
                    $"{_merge.Cells} cells, {_merge.MeshCount} meshes, {_merge.VertexCount} vertices, {_merge.BuildsDone} builds, " +
                    $"{PieceMeshCopy.GpuReads} meshes read from the GPU ({PieceMeshCopy.Failures} failed); " +
                    $"main thread {(_frames > 0 ? _frameMs / _frames : 0):F3} ms/frame, worst {_worstFrameMs:F1} ms, worst mesh {_merge.WorstApplyMs:F1} ms; " +
                    "left alone: " + string.Join(", ", Rejects.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key} {kv.Value}")));
                _frameMs = 0;
                _worstFrameMs = 0;
                _frames = 0;
            }
        }

        private static string Check(PieceEntry e) {
            if (e.W == null) { return "destroyed but still tracked"; }
            if (e.Hidden != (e.InMesh && !e.Suspended)) { return $"hidden {e.Hidden} but in mesh {e.InMesh}, suspended {e.Suspended}"; }
            if (e.Stage != Stage(e.W)) { return "health stage changed unseen"; }
            foreach (PiecePart p in e.Parts) {
                MeshRenderer r = p.Renderer;
                if (r == null) { return "renderer destroyed"; }
                if (e.Hidden && !r.forceRenderingOff) { return "renderer shown while merged"; }
                if (!e.Hidden && r.forceRenderingOff) { return "renderer hidden while not merged"; }
                Matrix4x4 now = r.localToWorldMatrix;
                for (int k = 0; k < 16; k++) {
                    if (Mathf.Abs(now[k] - p.LocalToWorld[k]) > 0.001f) { return "moved"; }
                }
            }
            Props props = ReadProps(e.W);
            if (!props.Transient && !props.Unknown && (props.UV != e.UVOffset || PieceMerge.SnowStep(props.Snow) != e.SnowBucket) && e.InMesh) {
                return "material values changed unseen";
            }
            return null;
        }
    }
}
