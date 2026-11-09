using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Grass Build Stall: grass patches are placed on a background thread instead of the main thread.
    //
    // ClutterSystem.GenerateVegPatch places every blade of a new grass patch on the main thread: for
    // each clutter type up to 80 seeded random candidates, each with forest and fractal noise, a
    // ground query and biome, altitude, slope and paint-mask checks. Every patch that comes into
    // range is built this way, a whole ring of them after a world load, a teleport, a graphics change
    // or a terrain edit, and the cost grows with the square of the grass distance.
    //
    // A replacing prefix copies what placement reads (the clutter settings and the heightmaps under
    // the patch) and queues the patch for a worker thread, returning it empty. The worker runs the
    // game's placement loop on the copies, with a copy of UnityEngine.Random's generator seeded the
    // same way, so it places the same blades; the generator is checked against the engine's before
    // the first patch. A ClutterSystem.LateUpdate postfix fills finished patches within a time budget,
    // creating each type's InstanceRenderer as the game does. A rebuilt patch keeps its old grass until
    // then, and a patch freed first drops its build. Patches that would spawn clutter other than
    // instanced grass, and the main menu's, are built the game's way. Stands down while HearthBelow is
    // loaded (as Fix Grass Ground Raycasts, whose ground answer the worker reproduces), next to FarLands
    // or SteadyFrame, which build grass themselves, and when another mod hooks a method it reproduces.
    //
    // Client: ClutterSystem needs a camera.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(ClutterSystem))]
    internal static class GrassBuildThreadPatch {
        private const string FixName = "Fix Grass Build Stall";

        // Main-thread time per frame for filling finished patches. At least one is filled each frame.
        private const double ApplyBudgetMs = 1.0;

        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Grass Build Thread",
                false,
                "Diagnostic. Also builds every grass patch the game's way on the main thread, compares it " +
                "blade for blade with the one built on the background thread, and logs any difference and a " +
                "running count. Costs the main-thread work this fix exists to avoid, so leave it off unless " +
                "you are validating it.",
                advanced: true);
        }

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(GrassBuildThreadPatch));

        private static readonly MethodInfo AllocatePatchMethod = AccessTools.DeclaredMethod(typeof(ClutterSystem), "AllocatePatch");
        private static readonly MethodInfo FreePatchMethod = AccessTools.DeclaredMethod(typeof(ClutterSystem), "FreePatch");
        private static readonly MethodInfo GenerateVegPatchMethod = AccessTools.DeclaredMethod(typeof(ClutterSystem), "GenerateVegPatch");

        // PatchData is private to ClutterSystem, so it is reached by reflection rather than by name.
        // Resolved when the fix first engages, not in the type initializer, which config binding runs
        // inside the plugin's Awake: a game update renaming one would otherwise stop the whole mod.
        private static AccessTools.FieldRef<object, List<GameObject>> PatchObjects;
        private static AccessTools.FieldRef<object, Vector3> PatchCenter;
        private static AccessTools.FieldRef<ClutterSystem, IDictionary> Patches;
        private static Func<ClutterSystem, object> AllocatePatch;

        private static bool ResolveMembers() {
            try {
                Type patchData = AccessTools.Inner(typeof(ClutterSystem), "PatchData");
                PatchObjects = AccessTools.FieldRefAccess<List<GameObject>>(patchData, "m_objects");
                PatchCenter = AccessTools.FieldRefAccess<Vector3>(patchData, "center");
                Patches = AccessTools.FieldRefAccess<ClutterSystem, IDictionary>("m_patches");
                AllocatePatch = AccessTools.MethodDelegate<Func<ClutterSystem, object>>(AllocatePatchMethod);
                return FreePatchMethod != null && GenerateVegPatchMethod != null;
            } catch (Exception e) {
                PatchHelper.WarnOnce($"{FixName}: a ClutterSystem member this fix needs could not be resolved ({e.Message}), so it is inactive.");
                return false;
            }
        }

        // A missed free would keep a dropped patch's build alive, and a missed terrain change would
        // place grass from stale heights, so every maintenance hook must be attached.
        private static readonly HookHealth Hooks = new HookHealth(
            typeof(GrassBuildThreadPatch),
            FixName,
            () => PatchHelper.HasHook(FreePatchMethod, typeof(FreePatchHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ClutterSystem), "LateUpdate"), typeof(ApplyHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ClutterSystem), "OnDestroy"), typeof(LifecycleHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(Heightmap), nameof(Heightmap.Regenerate)), typeof(TerrainHooks))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(Heightmap), "SetPaintMask"), typeof(TerrainHooks))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(Heightmap), "OnDestroy"), typeof(TerrainHooks)));

        // The worker reproduces these, so any other mod's hook on one of them would not run for the
        // grass it places, and the copied arrays are written without AddInstance.
        private static readonly TakeoverCheck[] Takeovers = {
            Reproduced(GenerateVegPatchMethod),
            Reproduced(AccessTools.DeclaredMethod(typeof(ClutterSystem), nameof(ClutterSystem.GetGroundInfo), new[] {
                typeof(Vector3), typeof(Vector3).MakeByRefType(), typeof(Vector3).MakeByRefType(),
                typeof(Heightmap).MakeByRefType(), typeof(Heightmap.Biome).MakeByRefType() })),
            Reproduced(AccessTools.DeclaredMethod(typeof(InstanceRenderer), nameof(InstanceRenderer.AddInstance),
                new[] { typeof(Vector3), typeof(Quaternion), typeof(float) })),
            Reproduced(AccessTools.DeclaredMethod(typeof(InstanceRenderer), nameof(InstanceRenderer.AddInstance),
                new[] { typeof(Matrix4x4) })),
            Reproduced(AccessTools.DeclaredMethod(typeof(Heightmap), nameof(Heightmap.GetBiome),
                new[] { typeof(Vector3), typeof(float), typeof(bool) })),
            Reproduced(AccessTools.DeclaredMethod(typeof(Heightmap), nameof(Heightmap.GetOceanDepth), new[] { typeof(Vector3) })),
            Reproduced(AccessTools.DeclaredMethod(typeof(Heightmap), nameof(Heightmap.GetVegetationMask), new[] { typeof(Vector3) })),
            Reproduced(AccessTools.DeclaredMethod(typeof(Heightmap), nameof(Heightmap.IsCleared), new[] { typeof(Vector3) })),
        };

        private static TakeoverCheck Reproduced(MethodBase target) => new TakeoverCheck(
            typeof(GrassBuildThreadPatch),
            target,
            HookKinds.Any,
            owners => $"Grass placement is changed by {owners}, so '{FixName}' stands down and grass " +
                      "patches are built on the main thread.");

        private static bool _loggedPrepare;
        private static bool _decided;
        private static bool _engaged;
        private static bool _broken;
        private static bool _bypass;

        // Main thread only.
        private static readonly List<GrassPatchJob> Pending = new List<GrassPatchJob>();
        private static readonly Dictionary<object, GrassPatchJob> PendingByPatch = new Dictionary<object, GrassPatchJob>();
        private static readonly Dictionary<Heightmap, GrassTerrain> Terrain = new Dictionary<Heightmap, GrassTerrain>();
        private static GrassClutterSpec[] _specs;
        private static ClutterSystem _specsFor;
        private static int _specsFrame = -1;

        // Shared with the worker, under its own lock.
        private static readonly Queue<GrassPatchJob> Queue = new Queue<GrassPatchJob>();
        private static Thread _worker;

        // ---- stand-downs ------------------------------------------------------------------------

        // Shared by the class and its nested hook classes, which Harmony patches one by one.
        private static bool PrepareAny() {
            if (!HearthBelowCompat.Loaded) { return true; }

            if (!_loggedPrepare) {
                _loggedPrepare = true;
                Logger.LogInfo($"HearthBelow is loaded, so '{FixName}' stands down: grass placement keeps the " +
                               "game's ground raycast for ground dug out with it.");
            }

            return false;
        }

        [HarmonyPrepare]
        private static bool Prepare() => PrepareAny();

        private static bool _gpuChecked;
        private static bool _gpuStoodDown;

        // Every plugin has loaded by the first ClutterSystem.Awake, which is not true when this mod
        // patches, and the GPU grass module first looks for other hooks after it.
        [HarmonyPatch(typeof(ClutterSystem), "Awake")]
        internal static class GpuGrassHook {
            [HarmonyPrepare]
            private static bool Prepare() => PrepareAny();

            [HarmonyPrefix]
            private static void Prefix() {
                if (_gpuChecked) { return; }
                _gpuChecked = true;
                if (!GpuGrassCompat.BuildsGrass) { return; }

                GpuGrassCompat.Unhook(GenerateVegPatchMethod, typeof(GrassBuildThreadPatch));
                _gpuStoodDown = true;

                FixRegistry.MarkStoodDown(typeof(GrassBuildThreadPatch));
                Logger.LogInfo($"{GpuGrassCompat.Name} builds the grass itself, so '{FixName}' stands down.");
            }
        }

        // Decided at the first patch of the session, once every mod has patched.
        private static bool Engaged() {
            if (ApiSwitch.Off || _broken || _gpuStoodDown) { return false; }

            if (!_decided) {
                _decided = true;
                _engaged = Decide();
            }

            // The ground fix can be turned off mid-session; the worker answers as it does.
            return _engaged && ClutterGroundDataPatch.Answering;
        }

        private static bool Decide() {
            if (!ResolveMembers()) { return false; }
            if (!Hooks.Healthy) { return false; }

            bool takenOver = false;
            foreach (TakeoverCheck check in Takeovers) { takenOver |= check.TakenOver; }
            if (takenOver) { return false; }

            if (!ClutterGroundDataPatch.Answering) {
                FixRegistry.MarkStoodDown(typeof(GrassBuildThreadPatch));
                Logger.LogInfo($"'Fix Grass Ground Raycasts' is not answering ground queries, so '{FixName}' stands " +
                               "down: the worker reproduces that fix's answer, not the game's raycast.");
                return false;
            }

            if (!UnityRandom.MatchesUnity(out string mismatch)) {
                FixRegistry.MarkStoodDown(typeof(GrassBuildThreadPatch));
                Logger.LogWarning($"{FixName} stands down: its copy of the game's random number generator does not " +
                                  $"match the engine's on this build ({mismatch}), so it would place grass differently.");
                return false;
            }

            return true;
        }

        // ---- queueing ---------------------------------------------------------------------------

        // Priority.Last and __runOriginal: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("GenerateVegPatch")]
        private static bool GenerateVegPatchPrefix(ClutterSystem __instance, Vector2Int patchID, float size,
            ref object __result, bool __runOriginal) {
            if (!__runOriginal) { return false; }
            if (_bypass || __instance.m_menuHack || !Engaged()) { return true; }

            Vector3 center = __instance.GetVegPatchCenter(patchID);
            float half = size / 2f;
            Heightmap.Biome biomes = __instance.GetPatchBiomes(center, half);
            if (biomes == Heightmap.Biome.None) {
                __result = null;
                return false;
            }

            GrassClutterSpec[] specs = Specs(__instance);
            if (specs == null || GrassPatchJob.SpawnsObjects(specs, biomes)) { return true; }

            GrassPatchJob job = new GrassPatchJob {
                PatchId = patchID,
                Center = center,
                Size = size,
                PatchBiomes = biomes,
                Quality = __instance.m_quality,
                AmountScale = __instance.m_amountScale,
                WaterLevel = __instance.m_waterLevel,
                Clutter = specs,
            };

            // A map under the patch with no data yet: the game's build answers that the same way.
            if (!GatherTerrain(job, center, half)) { return true; }

            // From the same terrain as the copies, so any difference is the worker's.
            if (Verify != null && Verify.Value) { job.Reference = BuildTheGamesWay(__instance, patchID, size); }

            object patch = AllocatePatch(__instance);
            PatchCenter(patch) = center;
            job.Patch = patch;

            // GeneratePatch destroys the replaced patch's objects as soon as this returns, which would
            // leave a gap until the build lands, so they move to this build and go when it does.
            IDictionary patches = Patches(__instance);
            object old = patches[patchID];
            if (old != null) {
                if (PendingByPatch.TryGetValue(old, out GrassPatchJob previous)) {
                    job.Retired.AddRange(previous.Retired);
                    previous.Retired.Clear();
                }

                List<GameObject> oldObjects = PatchObjects(old);
                job.Retired.AddRange(oldObjects);
                oldObjects.Clear();
            }

            PendingByPatch[patch] = job;
            Pending.Add(job);
            Enqueue(job);

            __result = patch;
            return false;
        }

        // The clutter list as copies, made once per frame and reused while nothing in it changes.
        private static GrassClutterSpec[] Specs(ClutterSystem clutter) {
            if (ReferenceEquals(clutter, _specsFor) && _specsFrame == Time.frameCount) { return _specs; }

            List<ClutterSystem.Clutter> list = clutter.m_clutter;
            bool same = ReferenceEquals(clutter, _specsFor) && _specs != null && _specs.Length == list.Count;
            for (int i = 0; same && i < list.Count; i++) { same = _specs[i] != null && _specs[i].SameAs(list[i]); }

            if (!same) {
                GrassClutterSpec[] specs = new GrassClutterSpec[list.Count];
                for (int i = 0; i < list.Count; i++) {
                    specs[i] = GrassClutterSpec.From(list[i]);
                    if (specs[i] == null) { return null; }
                }

                _specs = specs;
            }

            _specsFor = clutter;
            _specsFrame = Time.frameCount;
            return _specs;
        }

        // The zones under the patch's corners, with their maps as HeightmapLookupPatch files them.
        private static bool GatherTerrain(GrassPatchJob job, Vector3 center, float half) {
            Vector2s[] corners = {
                ZoneSystem.GetZone(new Vector3(center.x - half, 0f, center.z - half)),
                ZoneSystem.GetZone(new Vector3(center.x + half, 0f, center.z - half)),
                ZoneSystem.GetZone(new Vector3(center.x - half, 0f, center.z + half)),
                ZoneSystem.GetZone(new Vector3(center.x + half, 0f, center.z + half)),
            };

            List<Vector2s> zones = new List<Vector2s>(4);
            foreach (Vector2s zone in corners) {
                if (!zones.Contains(zone)) { zones.Add(zone); }
            }

            job.Zones = zones.ToArray();
            job.Terrain = new GrassTerrain[zones.Count];
            for (int i = 0; i < zones.Count; i++) {
                Vector3 zonePos = ZoneSystem.GetZonePos(zones[i]);
                if (!HeightmapLookupPatch.TryGetCached(zonePos, out Heightmap hmap, out Vector3 origin)) {
                    hmap = Heightmap.FindHeightmap(zonePos);
                    origin = hmap != null ? hmap.transform.position : Vector3.zero;
                }

                if (hmap == null) { continue; }

                if (!Terrain.TryGetValue(hmap, out GrassTerrain copy)) {
                    copy = GrassTerrain.Copy(hmap, origin);
                    if (copy == null) { return false; }
                    Terrain[hmap] = copy;
                }

                job.Terrain[i] = copy;
            }

            return true;
        }

        private static void Enqueue(GrassPatchJob job) {
            lock (Queue) {
                Queue.Enqueue(job);
                if (_worker == null) {
                    _worker = new Thread(WorkerLoop) {
                        IsBackground = true,
                        Name = "VCP grass build",
                        Priority = System.Threading.ThreadPriority.BelowNormal,
                    };
                    _worker.Start();
                }

                Monitor.Pulse(Queue);
            }
        }

        private static void WorkerLoop() {
            while (true) {
                GrassPatchJob job;
                lock (Queue) {
                    while (Queue.Count == 0) { Monitor.Wait(Queue); }
                    job = Queue.Dequeue();
                }

                try {
                    if (!job.Cancelled) { job.Build(); }
                } catch (Exception e) {
                    job.Error = e;
                } finally {
                    job.Done = true;
                }
            }
        }

        // ---- filling ----------------------------------------------------------------------------

        // After the frame's grass update, so patches it queued this frame are not looked at yet.
        [HarmonyPatch(typeof(ClutterSystem), "LateUpdate")]
        internal static class ApplyHook {
            [HarmonyPrepare]
            private static bool Prepare() => PrepareAny();

            [HarmonyPostfix]
            private static void Postfix(ClutterSystem __instance) {
                if (Pending.Count == 0) { return; }

                long start = Stopwatch.GetTimestamp();
                long budget = (long)(ApplyBudgetMs * Stopwatch.Frequency / 1000.0);
                int applied = 0;
                bool stop = false;
                int write = 0;

                for (int read = 0; read < Pending.Count; read++) {
                    GrassPatchJob job = Pending[read];
                    if (job.Cancelled) { continue; }

                    if (!stop && job.Done) {
                        if (applied > 0 && Stopwatch.GetTimestamp() - start > budget) {
                            stop = true;
                        } else {
                            PendingByPatch.Remove(job.Patch);
                            Fill(__instance, job);
                            applied++;
                            continue;
                        }
                    }

                    Pending[write++] = job;
                }

                Pending.RemoveRange(write, Pending.Count - write);
            }
        }

        private static void Fill(ClutterSystem clutter, GrassPatchJob job) {
            try {
                if (job.Error != null) { throw new InvalidOperationException("the worker failed", job.Error); }
                if (job.Reference != null) { CompareWithGame(job); }

                List<GameObject> objects = PatchObjects(job.Patch);
                Transform root = clutter.m_grassRoot.transform;
                float lodLimit = clutter.m_distance - clutter.m_grassPatchSize / 2f;

                for (int i = 0; i < job.Clutter.Length; i++) {
                    int count = job.Count[i];
                    if (count == 0) { continue; }

                    GameObject gameObject = UnityEngine.Object.Instantiate(job.Clutter[i].Prefab, job.Center, Quaternion.identity, root);
                    InstanceRenderer renderer = gameObject.GetComponent<InstanceRenderer>();
                    if (renderer.m_lodMaxDistance > lodLimit) { renderer.m_lodMaxDistance = lodLimit; }

                    // What AddInstance does per blade, as one copy. Grow keeps Fix Grass Instance
                    // Memory's small arrays big enough, and leaves the game's full-size one alone.
                    GrassInstanceMemoryPatch.Grow(renderer, count);
                    Array.Copy(job.Matrices, job.Start[i], renderer.m_instances, 0, count);
                    renderer.m_instanceCount = count;
                    renderer.m_dirtyBounds = true;

                    objects.Add(gameObject);
                }
            } catch (Exception e) {
                Break(clutter, job, e);
            } finally {
                DestroyRetired(job);
            }
        }

        // A failure stops the fix for the session and gives the patch back to the game's build.
        private static void Break(ClutterSystem clutter, GrassPatchJob job, Exception e) {
            if (!_broken) {
                _broken = true;
                FixRegistry.MarkStoodDown(typeof(GrassBuildThreadPatch));
                Logger.LogError($"{FixName} failed to build a grass patch and stands down for this session; grass " +
                                $"is built on the main thread from now on: {e}");
            }

            clutter.ResetGrass(job.Center, job.Size / 2f);
        }

        private static void DestroyRetired(GrassPatchJob job) {
            foreach (GameObject gameObject in job.Retired) {
                if (gameObject != null) { UnityEngine.Object.Destroy(gameObject); }
            }

            job.Retired.Clear();
        }

        private static void Cancel(GrassPatchJob job) {
            job.Cancelled = true;
            DestroyRetired(job);
        }

        // ---- maintenance ------------------------------------------------------------------------

        // ClearAll, TimeoutPatches and a rebuild all free the patch; its build is dropped with it.
        [HarmonyPatch(typeof(ClutterSystem), "FreePatch")]
        internal static class FreePatchHook {
            [HarmonyPrepare]
            private static bool Prepare() => PrepareAny();

            [HarmonyPrefix]
            private static void Prefix(object patch) {
                if (PendingByPatch.Count == 0 || patch == null || !PendingByPatch.TryGetValue(patch, out GrassPatchJob job)) { return; }

                PendingByPatch.Remove(patch);
                Cancel(job);
            }
        }

        // A scene change destroys the grass root and every patch with it, without freeing them.
        [HarmonyPatch(typeof(ClutterSystem), "OnDestroy")]
        internal static class LifecycleHook {
            [HarmonyPrepare]
            private static bool Prepare() => PrepareAny();

            [HarmonyPostfix]
            private static void Postfix() {
                foreach (GrassPatchJob job in Pending) { Cancel(job); }
                Pending.Clear();
                PendingByPatch.Clear();
                Terrain.Clear();
                _specs = null;
                _specsFor = null;
            }
        }

        // A map's heights, biomes, ocean depths and paint all change only here, so its copy is
        // dropped and made again the next time a patch needs it.
        [HarmonyPatch(typeof(Heightmap))]
        internal static class TerrainHooks {
            [HarmonyPrepare]
            private static bool Prepare() => PrepareAny();

            [HarmonyPostfix]
            [HarmonyPatch(nameof(Heightmap.Regenerate))]
            private static void RegeneratePostfix(Heightmap __instance) => Terrain.Remove(__instance);

            [HarmonyPostfix]
            [HarmonyPatch("SetPaintMask")]
            private static void SetPaintMaskPostfix(Heightmap __instance) => Terrain.Remove(__instance);

            [HarmonyPostfix]
            [HarmonyPatch("OnDestroy")]
            private static void OnDestroyPostfix(Heightmap __instance) => Terrain.Remove(__instance);
        }

        // ---- verification -----------------------------------------------------------------------

        private static int _compared;
        private static int _differed;
        private static int _reported;
        private static float _nextReport;

        // Builds the patch the game's way and keeps only its blades, each grass object's in order.
        private static List<Matrix4x4[]> BuildTheGamesWay(ClutterSystem clutter, Vector2Int patchId, float size) {
            object reference;
            _bypass = true;
            try {
                reference = GenerateVegPatchMethod.Invoke(clutter, new object[] { patchId, size });
            } finally {
                _bypass = false;
            }

            List<Matrix4x4[]> blades = new List<Matrix4x4[]>();
            if (reference == null) { return blades; }

            foreach (GameObject gameObject in PatchObjects(reference)) {
                InstanceRenderer renderer = gameObject.GetComponent<InstanceRenderer>();
                Matrix4x4[] copy = new Matrix4x4[renderer != null ? renderer.m_instanceCount : 0];
                if (renderer != null) { Array.Copy(renderer.m_instances, copy, copy.Length); }
                blades.Add(copy);

                gameObject.SetActive(false);
                UnityEngine.Object.Destroy(gameObject);
            }

            FreePatchMethod.Invoke(clutter, new[] { reference });
            return blades;
        }

        private static void CompareWithGame(GrassPatchJob job) {
            string difference = Difference(job.Reference, job);
            _compared++;
            if (difference != null) {
                _differed++;
                if (_reported < 20) {
                    _reported++;
                    Logger.LogWarning($"{FixName} verify: patch {job.PatchId} differs from the game's build: {difference}");
                }
            }

            if (Time.realtimeSinceStartup >= _nextReport) {
                _nextReport = Time.realtimeSinceStartup + 10f;
                Logger.LogInfo($"{FixName} verify: {_compared} patches compared, {_differed} differed.");
            }
        }

        private static string Difference(List<Matrix4x4[]> game, GrassPatchJob job) {
            int k = 0;
            for (int i = 0; i < job.Clutter.Length; i++) {
                int count = job.Count[i];
                if (count == 0) { continue; }

                if (k >= game.Count) { return $"clutter {i} ({job.Clutter[i].Prefab.name}) is missing from the game's build"; }

                Matrix4x4[] theirBlades = game[k++];
                if (theirBlades.Length != count) {
                    return $"clutter {i} ({job.Clutter[i].Prefab.name}): {theirBlades.Length} blades, {count} here";
                }

                for (int b = 0; b < count; b++) {
                    Matrix4x4 mine = job.Matrices[job.Start[i] + b];
                    Matrix4x4 theirs = theirBlades[b];
                    if (!mine.Equals(theirs)) {
                        Vector3 offset = (Vector3)mine.GetColumn(3) - (Vector3)theirs.GetColumn(3);
                        return $"clutter {i} ({job.Clutter[i].Prefab.name}) blade {b}: placed {offset.magnitude:0.######} m apart";
                    }
                }
            }

            return k != game.Count ? $"the game's build has {game.Count - k} more grass types" : null;
        }
    }
}
