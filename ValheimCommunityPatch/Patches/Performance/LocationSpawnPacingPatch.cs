using System.Diagnostics;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Location Spawn Hitch: every location proxy coming into range builds its whole prefab in the same frame.
    // Postfixes ZoneSystem.ShouldDelayProxyLocationSpawning to put off the rest once the frame's budget is spent.
    // Crossing into an area dense with villages, camps and ruins spreads them over frames instead of one long hitch.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZoneSystem))]
    internal static class LocationSpawnPacingPatch {
        private const string FixName = "Fix Location Spawn Hitch";

        internal static ConfigEntry<int> BudgetMs;

        internal static void BindConfig() {
            BudgetMs = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Location Spawn Budget",
                4,
                "Milliseconds per frame spent spawning locations (villages, camps, ruins) coming into range before " +
                "the rest wait a frame; the first always spawns. 0 is vanilla, which spawns every ready location at once.",
                advanced: true,
                valMin: 0,
                valMax: 50);
        }

        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            typeof(LocationSpawnPacingPatch),
            AccessTools.DeclaredMethod(typeof(ZoneSystem), nameof(ZoneSystem.ShouldDelayProxyLocationSpawning)),
            HookKinds.BoolPrefixes | HookKinds.Postfixes | HookKinds.Transpilers,
            owners => $"Location spawn timing is changed by {owners}, so '{FixName}' stands down and that " +
                      "mod's pace applies.");

        // Location spawn time in the frame _frame, in Stopwatch ticks.
        private static int _frame = -1;
        private static long _spent;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(LocationSpawnPacingPatch));

        // Set while LocationProxy.SetLocation spawns a location of a zone generated in full, whose
        // networked objects already exist, so it is never delayed.
        private static int _fullSpawnDepth;

        // Debug summary.
        private static float _summaryStart;
        private static int _spawned;
        private static int _delayed;
        private static long _worstFrame;

        // LocationProxy asks this first and, on true, holds its zone as loading and asks again next frame. A
        // postfix, so vanilla's check still refreshes the prefab keep-alive. Pacing rather than skipping: each
        // location is still built once and whole. Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(nameof(ZoneSystem.ShouldDelayProxyLocationSpawning))]
        private static void ShouldDelayPostfix(ZoneSystem __instance, int hash, ref bool __result) {
            if (__result || _frame != Time.frameCount || _fullSpawnDepth > 0) { return; }

            int budget = BudgetMs != null ? BudgetMs.Value : 0;
            if (budget <= 0 || ApiSwitch.Off || _spent < budget * Stopwatch.Frequency / 1000) { return; }

            // A location the game cannot find would retry forever and hold its zone.
            if (RunMode.InLoadingScreen() || __instance.GetLocation(hash) == null || Takeover.TakenOver) { return; }

            __result = true;
            _delayed++;
        }

        /// <summary>
        /// Counts other world-building work done this frame against the location budget, so a
        /// location coming into range waits for the next frame instead of piling on.
        /// </summary>
        internal static void ChargeFrame(long ticks) {
            int frame = Time.frameCount;
            if (frame != _frame) {
                _frame = frame;
                _spent = 0;
            }

            _spent += ticks;
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.SpawnProxyLocation))]
        internal static class SpawnTimingHook {
            [HarmonyPrefix]
            private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

            // A finalizer, so a spawn that throws still counts against the frame.
            [HarmonyFinalizer]
            private static void Finalizer(long __state) {
                long elapsed = Stopwatch.GetTimestamp() - __state;

                int frame = Time.frameCount;
                if (frame != _frame) {
                    _frame = frame;
                    _spent = 0;
                }

                _spent += elapsed;
                _spawned++;

                if (Logger.DebugEnabled) { RecordSummary(); }
            }
        }

        [HarmonyPatch(typeof(LocationProxy), nameof(LocationProxy.SetLocation))]
        internal static class FullSpawnHook {
            [HarmonyPrefix]
            private static void Prefix(bool spawnNow) {
                if (spawnNow) { _fullSpawnDepth++; }
            }

            [HarmonyFinalizer]
            private static void Finalizer(bool spawnNow) {
                if (spawnNow && _fullSpawnDepth > 0) { _fullSpawnDepth--; }
            }
        }

        private static void RecordSummary() {
            if (_spent > _worstFrame) { _worstFrame = _spent; }

            float now = Time.unscaledTime;
            if (_summaryStart <= 0f) { _summaryStart = now; }
            if (now - _summaryStart < 10f) { return; }

            if (_delayed > 0) {
                Logger.LogDebug(
                    $"Location spawns: {_spawned} in the last {now - _summaryStart:0} s, {_delayed} wait(s) for " +
                    $"a later frame, worst frame {_worstFrame * 1000.0 / Stopwatch.Frequency:0.0} ms.");
            }

            _summaryStart = now;
            _spawned = 0;
            _delayed = 0;
            _worstFrame = 0;
        }
    }
}
