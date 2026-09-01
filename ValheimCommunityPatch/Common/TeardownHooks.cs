using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using ValheimCommunityPatch.Patches.Performance;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>
    /// The single visit every destroyed ZNetView and WearNTear makes through this mod's registries,
    /// plus the destroy-storm instrument.
    /// </summary>
    /// <remarks>
    /// Why this exists: object teardown is the most boundary-correlated cost in the game
    /// (measured 7.21 ms/s in spiky seconds against 0.64 calm - an 11x ratio, where instantiate is
    /// 2.3x and ZNetView.LoadFields 1.4x), and before this file roughly 45% of it was this mod's
    /// own bookkeeping rather than vanilla's.
    ///
    /// Two defects produced that, and both are fixed here.
    ///
    /// 1. FOUR SEPARATE POSTFIXES on two methods. SectorInstanceIndexPatch hung one off
    ///    ZNetView.OnDestroy; SupportSleepPatch, WearSupportLookupPatch and WearCacheEventPatch each
    ///    hung one off WearNTear.OnDestroy. Each re-derived the same identity independently. They
    ///    are now one postfix per method, resolving the instance id once and handing it to each
    ///    registry in turn.
    ///
    /// 2. THE WRONG DICTIONARY KEY. Every one of those registries was keyed on a UnityEngine.Object
    ///    - ZNetView, WearNTear, Collider, Heightmap. UnityEngine.Object.GetHashCode() is cheap (it
    ///    returns the cached instance id), but Equals is not: it routes to CompareBaseObjects, a
    ///    native interop call, and a Dictionary pays that on EVERY probe, not only on collision.
    ///    The profile named the leaf directly and repeatedly - ObjectEqualityComparer`1.Equals under
    ///    HashSet`1.Remove, under Dictionary`2.Remove, under Dictionary`2.FindEntry. A four-collider
    ///    building piece paid roughly sixteen such probes on its way out. Every registry on this
    ///    path is now keyed on GetInstanceID(), so a probe is an integer compare.
    ///
    ///    MEASURED CAVEAT, learned the hard way the round after this shipped: GetInstanceID() is
    ///    NOT free in this build - it shows real self time in the profile, on the same order as the
    ///    native Equals it replaces. So this trade only pays where ONE id is amortised over MANY
    ///    probes, which is exactly the teardown shape (one id, ~16 probes; the whole teardown path
    ///    now spends ~0.3 ms/s inside GetInstanceID). It does NOT pay on a read path that does a
    ///    single probe per call: there it swaps one native call for another. Two such sites exist
    ///    and are marked in place - LightCostPatch.CustomUpdatePrefix and
    ///    WearSupportLookupPatch.ResolveSupport. Do not extend this pattern to a new single-probe
    ///    hot path without measuring it.
    ///
    /// The int key costs one invariant, stated once here because all four registries depend on it:
    /// an entry MUST be removed while its object is alive, because an int key cannot be checked for
    /// liveness the way an object key can, and Unity is entitled to reuse an instance id after the
    /// object holding it is gone. Every registry satisfies this the same way - it removes in the
    /// OnDestroy postfix below, and clears wholesale on ZNetScene.Shutdown - and each one already
    /// verifies its hooks attached before trusting itself. Note the object-keyed version was not
    /// actually safer here, only quieter: a leaked entry under an object key is a strong managed
    /// reference to a destroyed object, i.e. the same bug plus a memory leak.
    ///
    /// Ghosts are skipped outright on the ZNetView side. ZoneSystem pre-generates a zone by
    /// instantiating every vegetation prefab and every location piece in it and destroying them all
    /// in the same call (ZoneSystem.cs:589-604), and ZNetView.Awake returns before
    /// ZNetScene.AddInstance when m_ghostInit is set (ZNetView.cs:80-85), so those objects are never
    /// in the instance dictionary and never in our index. That is enforced on both sides rather than
    /// assumed: SectorInstanceIndexPatch.AddInstancePostfix refuses to index a ghost, and this
    /// refuses to look one up. Several hundred pointless probes per pre-generated zone.
    ///
    /// Both: a dedicated server tears down its own active-area objects through the same methods, and
    /// is the only side that runs ghost pre-generation at all.
    /// </remarks>
    [PatchSide(Side.Both)]
    internal static class TeardownHooks {
        internal static ConfigEntry<bool> LogStormStats;

        internal static void BindConfig() {
            LogStormStats = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Log Destroy Storm Stats",
                false,
                "Diagnostic. Buckets every frame by how many networked objects were torn down in " +
                "it and reports the frame time each bucket ran at, so a teardown burst can be " +
                "told apart from a steady drain. Also reports the unload pass's own object count " +
                "and wall-clock. Costs a counter increment per destroyed object; leave it off " +
                "unless you are measuring.",
                advanced: true);
        }

        // ---- the consolidated visit ----------------------------------------------------------

        [HarmonyPatch(typeof(ZNetView))]
        internal static class ViewHook {
            [HarmonyPostfix]
            [HarmonyPatch("OnDestroy")]
            private static void OnDestroyPostfix(ZNetView __instance) {
                if (_statsOn) { _viewDestroys++; }

                // Never indexed, so nothing to unindex - see the ghost note above.
                if (__instance.m_ghost) {
                    if (_statsOn) { _ghostDestroys++; }
                    return;
                }

                SectorInstanceIndexPatch.OnViewDestroyed(__instance);
            }
        }

        [HarmonyPatch(typeof(WearNTear))]
        internal static class PieceHook {
            // Order matches what the four separate postfixes did, which is the order Harmony
            // happened to run them in; none of the three reads state another writes, so this is
            // documentation rather than a constraint.
            [HarmonyPostfix]
            [HarmonyPatch("OnDestroy")]
            private static void OnDestroyPostfix(WearNTear __instance) {
                int id = __instance.GetInstanceID();

                SupportSleepPatch.OnPieceDestroyed(__instance, id);
                WearSupportLookupPatch.OnPieceDestroyed(id);
                WearCacheEventPatch.OnPieceDestroyed(__instance, id);
            }
        }

        // ---- the instrument ------------------------------------------------------------------

        // Read once a frame into a field so the two hot paths above test a bool, not a ConfigEntry.
        private static bool _statsOn;
        private static float _statsSince;

        private static int _viewDestroys;
        private static int _ghostDestroys;

        // The unload pass's own numbers, reported by ZoneDiffRemovalPatch.Execute. Kept apart from
        // the frame buckets on purpose: this is the MANAGED loop's cost, and the whole question the
        // instrument exists to answer is how much of a teardown frame is that loop versus Unity's
        // end-of-frame destruction flush, which no managed sampler can see.
        private static int _unloadPasses;
        private static int _unloadObjects;
        private static int _unloadWorstObjects;
        private static double _unloadMs;
        private static double _unloadWorstMs;

        // How much the frame budget deferred. A worst backlog far above the worst processed count
        // is the budget doing its job; the two being equal means it never bound and the storms are
        // arriving under the cap.
        private static int _unloadWorstBacklog;
        private static int _unloadDeferredPasses;

        // Frames bucketed by how many networked objects were torn down in them. The top bucket is
        // where a storm lands: a portal hop or a zone unload in a built-up area, where
        // ZNetScene.RemoveObjects' uncapped execution loop hands Unity the whole departed set at
        // once.
        private static readonly int[] BucketFloor = { 0, 1, 25, 100, 500 };
        private static readonly string[] BucketName = { "0", "1-24", "25-99", "100-499", "500+" };
        private static readonly long[] BucketFrames = new long[5];
        private static readonly double[] BucketMs = new double[5];
        private static readonly double[] BucketWorstMs = new double[5];

        private const float ReportIntervalSeconds = 30f;

        /// <summary>
        /// Frame boundary. ZNetScene.Update is the anchor because it runs every frame of every
        /// session on both sides.
        /// </summary>
        /// <remarks>
        /// The pairing here is the whole point of the instrument, so it is worth being explicit
        /// about. Unity defers GameObject destruction to the end of the frame that requested it, so
        /// the OnDestroy callbacks counted above ran at the end of the PREVIOUS frame - after that
        /// frame's LateUpdate, alongside the native teardown (PhysX actor removal, renderer and
        /// culling unregistration, hierarchy destruction) that is invisible to a managed sampler.
        /// Time.unscaledDeltaTime read here is the wall-clock length of that same previous frame,
        /// flush included. So counting at OnDestroy and measuring at the next Update attributes the
        /// storm to the frame that actually paid for it.
        ///
        /// unscaledDeltaTime rather than deltaTime so a paused or slow-motion game cannot rescale
        /// the measurement.
        /// </remarks>
        [HarmonyPatch(typeof(ZNetScene))]
        internal static class FrameHook {
            [HarmonyPrefix]
            [HarmonyPatch("Update")]
            private static void UpdatePrefix() {
                bool on = LogStormStats != null && LogStormStats.Value;

                if (!on) {
                    if (_statsOn) {
                        LogSummary("final");
                        Clear();
                        _statsOn = false;
                    }
                    return;
                }

                if (!_statsOn) {
                    _statsOn = true;
                    Clear();
                    _statsSince = Time.unscaledTime;
                    Logger.LogInfo(
                        "Destroy storm stats: on. Bucketing frames by teardown count; a summary " +
                        "follows every 30 s.");
                    return;
                }

                RecordFrame(_viewDestroys, Time.unscaledDeltaTime * 1000f);
                _viewDestroys = 0;

                if (Time.unscaledTime - _statsSince >= ReportIntervalSeconds) {
                    LogSummary("periodic");
                    Clear();
                    _statsSince = Time.unscaledTime;
                }
            }
        }

        /// <summary>Reported by the unload pass so its managed cost can be separated from the flush.</summary>
        internal static bool StatsOn => _statsOn;

        internal static void NoteUnloadPass(int processed, int backlog, double milliseconds) {
            if (!_statsOn) { return; }

            _unloadPasses++;
            _unloadObjects += processed;
            _unloadMs += milliseconds;
            if (processed > _unloadWorstObjects) { _unloadWorstObjects = processed; }
            if (milliseconds > _unloadWorstMs) { _unloadWorstMs = milliseconds; }
            if (backlog > _unloadWorstBacklog) { _unloadWorstBacklog = backlog; }
            if (backlog > processed) { _unloadDeferredPasses++; }
        }

        private static void RecordFrame(int destroys, float frameMs) {
            int bucket = 0;
            for (int i = BucketFloor.Length - 1; i >= 0; i--) {
                if (destroys >= BucketFloor[i]) { bucket = i; break; }
            }

            BucketFrames[bucket]++;
            BucketMs[bucket] += frameMs;
            if (frameMs > BucketWorstMs[bucket]) { BucketWorstMs[bucket] = frameMs; }
        }

        private static void Clear() {
            _viewDestroys = 0;
            _ghostDestroys = 0;
            _unloadPasses = 0;
            _unloadObjects = 0;
            _unloadWorstObjects = 0;
            _unloadMs = 0.0;
            _unloadWorstMs = 0.0;
            _unloadWorstBacklog = 0;
            _unloadDeferredPasses = 0;

            for (int i = 0; i < BucketFrames.Length; i++) {
                BucketFrames[i] = 0;
                BucketMs[i] = 0.0;
                BucketWorstMs[i] = 0.0;
            }
        }

        private static void LogSummary(string kind) {
            long frames = 0;
            for (int i = 0; i < BucketFrames.Length; i++) { frames += BucketFrames[i]; }
            if (frames == 0) { return; }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append($"Destroy storm stats ({kind}), {frames} frame(s) over ")
              .Append($"{Time.unscaledTime - _statsSince:F0} s:");

            for (int i = 0; i < BucketFrames.Length; i++) {
                if (BucketFrames[i] == 0) { continue; }
                sb.Append($" | {BucketName[i]} destroys: {BucketFrames[i]} frame(s), ")
                  .Append($"mean {BucketMs[i] / BucketFrames[i]:F1} ms, worst {BucketWorstMs[i]:F0} ms");
            }

            // The comparison that answers the question: if mean frame time climbs steeply with the
            // bucket, the end-of-frame flush is the hitch. If it is flat, teardown volume is not
            // what the stalls are made of and the storm thesis is wrong.
            sb.Append($" || ghost destroys {_ghostDestroys}");

            if (_unloadPasses > 0) {
                sb.Append($" || unload pass: {_unloadPasses} pass(es), {_unloadObjects} object(s), ")
                  .Append($"{_unloadMs:F0} ms managed, worst pass {_unloadWorstObjects} object(s) / ")
                  .Append($"{_unloadWorstMs:F1} ms")
                  .Append($", worst backlog {_unloadWorstBacklog}, {_unloadDeferredPasses} pass(es) capped");
            }

            Logger.LogInfo(sb.ToString());
        }
    }
}
