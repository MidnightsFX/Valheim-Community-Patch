using System.Diagnostics;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Background Zone Pacing: background zone pre-generation waits a tick when the frame is
    // already struggling.
    //
    // ZoneSystem.Update generates one full zone per 100 ms tick, entirely inside one frame, with
    // no awareness of frame time. Most of those are ghost zones from CreateGhostZones: pre-
    // generation of the wider ring around the host and each peer, with no same-frame consumer.
    // Walking into fresh terrain is one full generation burst every 100 ms.
    //
    // A prefix on CreateGhostZones skips the tick when the previous frame exceeded the budget or a
    // cooldown is armed; a postfix times each generation that ran and arms a cooldown of N ticks
    // when it blew the budget on its own. A starvation guard runs the generation regardless after
    // a few consecutive skips. The same zones generate identically, only the timing spreads out,
    // and zones a player actually enters (CreateLocalZones) are never touched.
    //
    // Another mod can also pause ghost generation outright through the mod API's SkipGhostZones,
    // as SteadyFrame does while its loading screen is up. That pause sits outside the pacing: it
    // neither counts toward the starvation guard nor is overridden by it. A watchdog ignores a
    // flag left set for longer than any loading screen, so a mod that never clears it cannot
    // stop world generation for the session.
    //
    // Server: only the world owner reaches CreateGhostZones.
    [PatchSide(Side.Server)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZoneSystem))]
    internal static class ZoneGenPacingPatch {
        internal static ConfigEntry<int> BudgetMs;
        internal static ConfigEntry<int> CooldownTicks;

        // After this many consecutive skipped ticks, generate regardless.
        private const int MaxConsecutiveSkips = 4;

        private static int _decisionFrame = -1;
        private static bool _skipThisFrame;
        private static int _consecutiveSkips;
        private static int _cooldownRemaining;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(ZoneGenPacingPatch));

        // Set and cleared by other mods through the API, from the main thread.
        private static volatile bool _skipGhostZones;

        // How long a pause may last before the watchdog ignores it. Loading screens take seconds.
        private const float MaxSkipSeconds = 120f;

        private static float _skipSince = -1f;
        private static bool _skipExpired;

        internal static bool SkipGhostZones {
            get => _skipGhostZones;
            set => _skipGhostZones = value;
        }

        /// <summary>True when the CreateGhostZones prefix that honours SkipGhostZones is attached.</summary>
        internal static bool SkipGhostZonesSupported =>
            PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(ZoneSystem), "CreateGhostZones"), typeof(ZoneGenPacingPatch));

        internal static void BindConfig() {
            BudgetMs = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Zone Generation Frame Budget",
                30,
                "Milliseconds: a background zone generation tick is deferred when the previous " +
                "frame exceeded this, and a generation that itself took longer than this arms " +
                "the cooldown. Lower spreads generation out more aggressively.",
                advanced: true,
                valMin: 10,
                valMax: 100);

            CooldownTicks = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Zone Generation Cooldown Ticks",
                2,
                "How many 100ms ticks to wait after an expensive background zone generation " +
                "before the next one. 0 disables the cooldown and paces on frame pressure alone.",
                advanced: true,
                valMin: 0,
                valMax: 10);
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("CreateGhostZones")]
        private static bool CreateGhostZonesPrefix(ref bool __result, out long __state, bool __runOriginal) {
            __state = 0;
            if (!__runOriginal) { return false; }

            if (SkipRequested()) {
                __result = false;
                return false;
            }

            // Only the pacing is turned off; SkipGhostZones, which other mods rely on, keeps working.
            if (ApiSwitch.Off) { return true; }

            // One decision per frame, shared by the host's call and each peer's.
            int frame = Time.frameCount;
            if (frame != _decisionFrame) {
                _decisionFrame = frame;

                float previousFrameMs = Time.unscaledDeltaTime * 1000f;
                int budget = BudgetMs != null ? BudgetMs.Value : 30;
                bool wantSkip = previousFrameMs > budget || _cooldownRemaining > 0;

                if (wantSkip && _consecutiveSkips < MaxConsecutiveSkips) {
                    _skipThisFrame = true;
                    _consecutiveSkips++;
                    if (_cooldownRemaining > 0) { _cooldownRemaining--; }
                } else {
                    _skipThisFrame = false;
                    _consecutiveSkips = 0;
                    _cooldownRemaining = 0;
                }
            }

            if (_skipThisFrame) {
                __result = false;
                return false;
            }

            __state = Stopwatch.GetTimestamp();
            return true;
        }

        private static bool SkipRequested() {
            if (!_skipGhostZones) {
                _skipSince = -1f;
                _skipExpired = false;
                return false;
            }

            if (_skipExpired) { return false; }

            float now = Time.unscaledTime;
            if (_skipSince < 0f) { _skipSince = now; }

            if (now - _skipSince > MaxSkipSeconds) {
                _skipExpired = true;
                Logger.LogWarning(
                    $"Another mod has paused background zone generation for over {MaxSkipSeconds:0} s " +
                    "through SkipGhostZones and not resumed it, so the pause is ignored until that mod " +
                    "clears it.");
                return false;
            }

            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch("CreateGhostZones")]
        private static void CreateGhostZonesPostfix(long __state) {
            if (__state == 0) { return; }
            if (CooldownTicks == null || CooldownTicks.Value <= 0) { return; }

            double elapsedMs = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            int budget = BudgetMs != null ? BudgetMs.Value : 30;

            if (elapsedMs > budget) { _cooldownRemaining = CooldownTicks.Value; }
        }
    }
}
