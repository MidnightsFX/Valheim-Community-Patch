using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Terrain Builder Throughput: the terrain build thread sleeps only when idle and holds
    // more finished results.
    //
    // HeightmapBuilder.BuildThread sleeps 10 ms after every iteration, including one that just
    // finished a build with more work queued, which caps terrain generation far below what the
    // thread could do and extends every zone spawn's wait for terrain. Its ready queue is capped
    // at 16 with silent oldest-first eviction, and the distant-terrain ring alone keeps 9 in
    // flight, so finished results are evicted before they are consumed and rebuilt from scratch.
    //
    // A prefix replaces the loop with the same code and the same lock discipline, sleeping only
    // when the queue was empty, and with a configurable ready cap (default 32). The thread enters
    // BuildThread once, when the singleton is created, so the patch has to be in place before
    // that; Prepare logs if it was not.
    //
    // Because this loop replaces the one other mods would patch, it carries their hooks through
    // the mod API (Common/APIReceiver.cs): zones another mod asks for are built first, every
    // finished build is announced, and finished builds can be handed back in.
    //
    // Both: servers generate terrain for every ghost zone around every peer.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(HeightmapBuilder))]
    internal static class HeightmapBuilderThroughputPatch {
        internal static ConfigEntry<int> ReadyCap;

        internal static void BindConfig() {
            ReadyCap = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Terrain Builder Ready Cap",
                32,
                "How many finished terrain results the build thread may hold before discarding the " +
                "oldest. Each is roughly 100 KB. Vanilla holds 16, which the distant-terrain ring " +
                "alone nearly fills.",
                advanced: true,
                valMin: 16,
                valMax: 128);
        }

        private const int VanillaReadyCap = 16;

        // Set by the build thread once this loop is the one it runs.
        private static volatile bool _loopRunning;

        // The ready cap the build thread enforces right now: this fix's when its loop is running,
        // else vanilla's. Read by Fix Destination Terrain Delay, which must not overfill it.
        internal static int ReadyCapInForce => _loopRunning && ReadyCap != null ? ReadyCap.Value : VanillaReadyCap;

        /// <summary>True while this loop, and so the API's priority and finished-build hooks, is the one the build thread runs.</summary>
        internal static bool LoopRunning => _loopRunning;

        [HarmonyPrepare]
        private static bool Prepare() {
            if (HeightmapBuilder.m_instance != null) {
                FixRegistry.MarkStoodDown(typeof(HeightmapBuilderThroughputPatch));
                Logger.LogWarning(
                    "The terrain build thread was already running before this patch applied, so " +
                    "'Fix Terrain Builder Throughput' is inert this session.");
            }

            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch("BuildThread")]
        private static bool BuildThreadPrefix(HeightmapBuilder __instance) {
            ZLog.Log((object)"Builder started");
            _loopRunning = true;
            bool stop = false;
            while (!stop) {
                // Checked and taken under one lock: SubmitFinishedBuild can empty the queue from
                // another thread, which vanilla's two separate locks would then index past.
                HeightmapBuilder.HMBuildData data = null;
                lock (__instance.m_lock) {
                    if (__instance.m_toBuild.Count > 0) { data = PickNext(__instance.m_toBuild); }
                }

                bool haveWork = data != null;
                if (haveWork) {
                    __instance.Build(data);

                    lock (__instance.m_lock) {
                        __instance.m_toBuild.Remove(data);
                        __instance.m_ready.Add(data);
                        int cap = ReadyCap != null ? ReadyCap.Value : VanillaReadyCap;
                        while (__instance.m_ready.Count > cap) { __instance.m_ready.RemoveAt(0); }
                    }

                    // Outside the lock, so a handler may hand a build back in.
                    RaiseBuildFinished(data);
                }

                if (!haveWork) { Thread.Sleep(10); }

                lock (__instance.m_lock) { stop = __instance.m_stop; }
            }

            _loopRunning = false;
            return false;
        }

        // ---- Mod API: zones built first ----

        // Zone -> its place in the caller's list. Replaced whole, never changed in place, so the
        // build thread reads it without a lock.
        private static volatile Dictionary<Vector2s, int> _priority;

        /// <summary>
        /// Builds these zones before anything else queued, in the order given, until replaced or
        /// cleared. Zones queued after this call are covered too. Distant-terrain tiles are never
        /// promoted.
        /// </summary>
        internal static void SetPriorityZones(IList<Vector2s> zones) {
            if (zones == null || zones.Count == 0) {
                _priority = null;
                return;
            }

            Dictionary<Vector2s, int> rank = new Dictionary<Vector2s, int>(zones.Count);
            for (int i = 0; i < zones.Count; i++) {
                if (!rank.ContainsKey(zones[i])) { rank.Add(zones[i], i); }
            }

            _priority = rank;
        }

        internal static void ClearPriorityZones() => _priority = null;

        // Called under m_lock with at least one entry queued. The queue holds a few dozen entries
        // at most, so a scan per build is nothing next to the build itself.
        private static HeightmapBuilder.HMBuildData PickNext(List<HeightmapBuilder.HMBuildData> toBuild) {
            Dictionary<Vector2s, int> priority = _priority;
            if (priority == null) { return toBuild[0]; }

            HeightmapBuilder.HMBuildData best = null;
            int bestRank = int.MaxValue;
            for (int i = 0; i < toBuild.Count; i++) {
                HeightmapBuilder.HMBuildData candidate = toBuild[i];
                if (candidate.m_distantLod) { continue; }

                if (priority.TryGetValue(ZoneSystem.GetZone(candidate.m_center), out int rank) && rank < bestRank) {
                    best = candidate;
                    bestRank = rank;
                    if (rank == 0) { break; }
                }
            }

            return best ?? toBuild[0];
        }

        // ---- Mod API: finished builds ----

        private static readonly object HandlerGate = new object();

        // Copy-on-write, so the build thread raises without taking HandlerGate.
        private static volatile Action<HeightmapBuilder.HMBuildData>[] _handlers = new Action<HeightmapBuilder.HMBuildData>[0];

        internal static void AddBuildFinishedHandler(Action<HeightmapBuilder.HMBuildData> handler) {
            if (handler == null) { return; }

            lock (HandlerGate) {
                Action<HeightmapBuilder.HMBuildData>[] old = _handlers;
                Action<HeightmapBuilder.HMBuildData>[] next = new Action<HeightmapBuilder.HMBuildData>[old.Length + 1];
                Array.Copy(old, next, old.Length);
                next[old.Length] = handler;
                _handlers = next;
            }
        }

        internal static void RemoveBuildFinishedHandler(Action<HeightmapBuilder.HMBuildData> handler) {
            if (handler == null) { return; }

            lock (HandlerGate) {
                Action<HeightmapBuilder.HMBuildData>[] old = _handlers;
                int index = Array.IndexOf(old, handler);
                if (index < 0) { return; }

                Action<HeightmapBuilder.HMBuildData>[] next = new Action<HeightmapBuilder.HMBuildData>[old.Length - 1];
                Array.Copy(old, 0, next, 0, index);
                Array.Copy(old, index + 1, next, index, old.Length - index - 1);
                _handlers = next;
            }
        }

        // On the build thread. A handler that throws is dropped, so another mod's bug costs it its
        // hook rather than stopping terrain generation for the session.
        private static void RaiseBuildFinished(HeightmapBuilder.HMBuildData data) {
            Action<HeightmapBuilder.HMBuildData>[] handlers = _handlers;
            for (int i = 0; i < handlers.Length; i++) {
                try {
                    handlers[i](data);
                } catch (Exception ex) {
                    RemoveBuildFinishedHandler(handlers[i]);
                    Logger.LogError(
                        $"A terrain build handler from {handlers[i].Method?.DeclaringType?.Assembly.GetName().Name} " +
                        $"threw and has been removed: {ex}");
                }
            }
        }

        /// <summary>
        /// Puts a finished build into the ready list, as if the build thread had just made it, so
        /// the next request for that terrain takes it instead of building it again. False when
        /// there is no builder, the data was never built or belongs to another world, or an equal
        /// build is already waiting.
        /// </summary>
        /// <remarks>
        /// Any thread. If the build thread is building the same terrain at that moment, the ready
        /// list briefly holds both; the first is taken and the other ages out under the cap.
        /// Heightmap only copies out of a build, so one instance can be handed in again and again.
        /// </remarks>
        internal static bool SubmitFinishedBuild(HeightmapBuilder.HMBuildData data) {
            if (data == null || data.m_baseHeights == null || data.m_baseMask == null || data.m_cornerBiomes == null) {
                return false;
            }

            if (data.m_worldGen == null || data.m_worldGen != WorldGenerator.instance) { return false; }

            // The private field, not the property, which would start a builder or warn after
            // shutdown. Dispose nulls the lock.
            HeightmapBuilder builder = HeightmapBuilder.m_instance;
            object gate = builder?.m_lock;
            if (gate == null || HeightmapBuilder.hasBeenDisposed) { return false; }

            lock (gate) {
                List<HeightmapBuilder.HMBuildData> ready = builder.m_ready;
                for (int i = 0; i < ready.Count; i++) {
                    if (Matches(ready[i], data)) { return false; }
                }

                // Vanilla's loop checks for work and takes it under separate locks, so emptying
                // its queue in between would crash its thread. Under vanilla the queued copy is
                // built anyway and ages out unused.
                if (_loopRunning) {
                    List<HeightmapBuilder.HMBuildData> toBuild = builder.m_toBuild;
                    for (int i = toBuild.Count - 1; i >= 0; i--) {
                        if (Matches(toBuild[i], data)) { toBuild.RemoveAt(i); }
                    }
                }

                ready.Add(data);
                int cap = ReadyCapInForce;
                while (ready.Count > cap) { ready.RemoveAt(0); }
            }

            return true;
        }

        private static bool Matches(HeightmapBuilder.HMBuildData entry, HeightmapBuilder.HMBuildData data) =>
            entry.IsEqual(data.m_center, data.m_width, data.m_scale, data.m_distantLod, data.m_worldGen);
    }
}
