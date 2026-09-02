using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using ValheimCommunityPatch.Patches.Performance;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>
    /// One OnDestroy postfix per destroyed ZNetView and WearNTear, fanning out to every registry
    /// in this mod that tracks them, plus the "Log Destroy Storm Stats" diagnostic.
    /// </summary>
    /// <remarks>
    /// Object teardown is the most zone-boundary-correlated cost in the game, and before this
    /// file about half of it was this mod's own bookkeeping: four separate postfixes each
    /// re-deriving the same identity, and registries keyed on UnityEngine.Object, whose Equals is
    /// a native call paid on every dictionary probe. So there is one postfix per method here,
    /// resolving GetInstanceID() once and handing it to each registry, and every registry on
    /// this path is keyed on that int.
    ///
    /// The int key costs one invariant, stated once here for all of them: an entry must be
    /// removed while its object is alive, because Unity may reuse an instance id after the
    /// object is gone. Every registry removes in the OnDestroy postfix below and clears on
    /// ZNetScene.Shutdown. GetInstanceID() is itself a native call, so the int key only pays
    /// where one id serves many probes, as it does on teardown; do not extend it to a
    /// single-probe read path without measuring.
    ///
    /// Ghost views (ZoneSystem pre-generation) never enter ZNetScene.m_instances and so never
    /// enter the index. They are skipped here and refused by
    /// SectorInstanceIndexPatch.AddInstancePostfix.
    ///
    /// Both: a dedicated server tears down its own active-area objects through the same methods.
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

                if (__instance.m_ghost) {
                    if (_statsOn) { _ghostDestroys++; }
                    return;
                }

                SectorInstanceIndexPatch.OnViewDestroyed(__instance);
            }
        }

        [HarmonyPatch(typeof(WearNTear))]
        internal static class PieceHook {
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

        // Read once a frame into a field so the hot paths above test a bool, not a ConfigEntry.
        private static bool _statsOn;
        private static float _statsSince;

        private static int _viewDestroys;
        private static int _ghostDestroys;

        // The unload pass's own numbers, reported by ZoneDiffRemovalPatch.Execute. Kept apart
        // from the frame buckets: this is the managed loop's cost, and the question the
        // instrument answers is how much of a teardown frame is that loop versus Unity's
        // end-of-frame destruction flush, which no managed sampler can see.
        private static int _unloadPasses;
        private static int _unloadObjects;
        private static int _unloadWorstObjects;
        private static double _unloadMs;
        private static double _unloadWorstMs;
        private static int _unloadWorstBacklog;
        private static int _unloadDeferredPasses;

        // Frames bucketed by how many networked objects were torn down in them.
        private static readonly int[] BucketFloor = { 0, 1, 25, 100, 500 };
        private static readonly string[] BucketName = { "0", "1-24", "25-99", "100-499", "500+" };
        private static readonly long[] BucketFrames = new long[5];
        private static readonly double[] BucketMs = new double[5];
        private static readonly double[] BucketWorstMs = new double[5];

        private const float ReportIntervalSeconds = 30f;

        // Unity destroys GameObjects at the end of the frame that requested it, so the OnDestroy
        // callbacks counted above ran at the end of the previous frame, alongside the native
        // teardown. Time.unscaledDeltaTime read here is that same frame's wall-clock length, so
        // counting at OnDestroy and measuring at the next Update attributes the storm to the
        // frame that actually paid for it.
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

        internal static bool StatsOn => _statsOn;

        /// <summary>Reported by the unload pass so its managed cost can be separated from the flush.</summary>
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
