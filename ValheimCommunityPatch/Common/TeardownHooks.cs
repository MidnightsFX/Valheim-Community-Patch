using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using ValheimCommunityPatch.Patches.Performance;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    // One OnDestroy postfix each for ZNetView and WearNTear, handing the destroyed object to every registry
    // that tracks it, plus the "Log Destroy Storm Stats" diagnostic.
    [PatchSide(Side.Both)]
    internal static class TeardownHooks {
        internal static ConfigEntry<bool> LogStormStats;

        internal static void BindConfig() {
            LogStormStats = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Log Destroy Storm Stats",
                false,
                "Diagnostic. Logs every 30 s how long frames took, grouped by how many networked objects " +
                "each one tore down, plus the unload pass's own object count and time.",
                advanced: true);
        }

        // ---- the consolidated visit ----------------------------------------------------------

        // Registries on this path key on GetInstanceID(), resolved once here, not on the object, whose
        // Equals is a native call on every probe. Unity may reuse an id once its object is gone, so each
        // registry removes its entry in these postfixes and clears on ZNetScene.Shutdown. GetInstanceID()
        // is native too, so the int key only pays where one id serves many probes; measure before using
        // it on a single-probe read path.
        [HarmonyPatch(typeof(ZNetView))]
        internal static class ViewHook {
            [HarmonyPostfix]
            [HarmonyPatch("OnDestroy")]
            private static void OnDestroyPostfix(ZNetView __instance) {
                if (_statsOn) { _viewDestroys++; }

                // Ghost views (ZoneSystem pre-generation) never enter ZNetScene.m_instances or any registry.
                if (__instance.m_ghost) {
                    if (_statsOn) { _ghostDestroys++; }
                    return;
                }

                SectorInstanceIndexPatch.OnViewDestroyed(__instance);
                Patches.Correctness.DeadInstancePatch.OnViewDestroyed(__instance);
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
                PieceRenderBatchPatch.OnPieceDestroyed(id);
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
