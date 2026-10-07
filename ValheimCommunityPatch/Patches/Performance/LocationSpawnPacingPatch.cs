using System.Diagnostics;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Location Spawn Hitch: locations coming into range spawn one per frame plus as many more as
    // a small time budget allows, instead of every ready location in the same frame.
    //
    // A location (a village, a fortress, a camp) arrives as a LocationProxy, which instantiates the
    // whole location prefab synchronously the moment it is created. The object pass creates at
    // least ten objects at a time, so crossing into an area dense with locations can build several
    // in one frame, one long hitch.
    //
    // The game already has a way to put a location off: LocationProxy asks
    // ZoneSystem.ShouldDelayProxyLocationSpawning first, and on true flags its zone as loading
    // (so objects inside it wait, as for a location whose prefab is still loading) and asks again
    // every frame. A postfix makes that answer true when locations spawned this frame have already
    // used the budget; the first location of a frame always goes. Vanilla's own check runs first
    // either way, so its prefab keep-alive still refreshes. Nothing is delayed behind a loading
    // screen, for a location the game cannot find (it would retry forever and hold its zone), or
    // while a zone is being generated in full, when the location's networked objects already exist.
    //
    // This paces work rather than checking, against the usual rule, because nothing repeats: each
    // location is still built exactly once and whole, the object pass is untouched, and a waiting
    // location costs one vanilla delay check per frame. A single large location still costs its
    // own frame; what goes is several stacking into one. Stands down when another mod changes the
    // delay check.
    //
    // Client: only a game with a local player streams locations around it.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(ZoneSystem))]
    internal static class LocationSpawnPacingPatch {
        private const string FixName = "Fix Location Spawn Hitch";

        internal static ConfigEntry<int> BudgetMs;

        internal static void BindConfig() {
            BudgetMs = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Location Spawn Budget",
                4,
                "Milliseconds per frame spent spawning locations (villages, camps, ruins) coming into " +
                "range before the rest wait for the next frame. The first location in a frame always " +
                "spawns, and nothing waits behind a loading screen. 0 restores vanilla, which spawns " +
                "every ready location at once.",
                advanced: true,
                valMin: 0,
                valMax: 50);
        }

        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            AccessTools.DeclaredMethod(typeof(ZoneSystem), nameof(ZoneSystem.ShouldDelayProxyLocationSpawning)),
            HookKinds.BoolPrefixes | HookKinds.Postfixes | HookKinds.Transpilers,
            owners => $"Location spawn timing is changed by {owners}, so '{FixName}' stands down and that " +
                      "mod's pace applies.");

        // Location spawn time in the frame _frame, in Stopwatch ticks.
        private static int _frame = -1;
        private static long _spent;

        // Set while LocationProxy.SetLocation spawns a location of a zone generated in full.
        private static int _fullSpawnDepth;

        // Debug summary.
        private static float _summaryStart;
        private static int _spawned;
        private static int _delayed;
        private static long _worstFrame;

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(nameof(ZoneSystem.ShouldDelayProxyLocationSpawning))]
        private static void ShouldDelayPostfix(ZoneSystem __instance, int hash, ref bool __result) {
            if (__result || _frame != Time.frameCount || _fullSpawnDepth > 0) { return; }

            int budget = BudgetMs != null ? BudgetMs.Value : 0;
            if (budget <= 0 || _spent < budget * Stopwatch.Frequency / 1000) { return; }

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
