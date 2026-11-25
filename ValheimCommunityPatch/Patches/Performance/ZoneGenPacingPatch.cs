using System.Diagnostics;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Background Zone Pacing: background zone generation builds a whole zone every 100 ms, however slow the frame.
    // Prefixes CreateGhostZones to skip a tick after a slow frame or a slow generation, with a starvation guard.
    // Pre-generation spreads out instead of piling onto slow frames; zones players enter are untouched.
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

        // Set and cleared by other mods through the API, from the main thread, as SteadyFrame does
        // behind its loading screen. Outside the pacing: the starvation guard neither counts nor
        // overrides it.
        private static volatile bool _skipGhostZones;

        // How long a pause may last before the watchdog ignores it, so a mod that never clears it
        // cannot stop world generation for the session. Loading screens take seconds.
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
                "Milliseconds: background zone generation waits a tick after a frame longer than this, and a " +
                "generation that takes longer arms the cooldown. Lower spreads generation out more.",
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
