using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Build Menu Language Subscription: piece buttons re-subscribe on every Setup; the event list grows forever.
    // A transpiler drops the Delegate.Combine in Setup; one handler added in BuildUi.Awake refreshes the shown buttons.
    // Opening the hammer menu or switching categories stops allocating megabytes and keeping destroyed buttons alive.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(BuildUiPieceButton))]
    internal static class BuildMenuLanguageEventPatch {
        // The menu whose buttons the handler refreshes; the game has one, under Hud.
        private static BuildUi _buildUi;

        private static readonly FixSwitch ApiSwitch = CreateSwitch();

        // Setup only skips its subscription when the handler that stands in for it is attached.
        private static readonly HookHealth Hooks = new HookHealth(
            typeof(BuildMenuLanguageEventPatch),
            "Build menu language subscription",
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(BuildUi), "Awake"), typeof(BuildUiHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(BuildUi), "OnDestroy"), typeof(BuildUiHook)));

        private static readonly MethodInfo CombineMethod = AccessTools.Method(
            typeof(Delegate), nameof(Delegate.Combine), new[] { typeof(Delegate), typeof(Delegate) });

        private static readonly MethodInfo KeepSubscriptionMethod =
            AccessTools.Method(typeof(BuildMenuLanguageEventPatch), nameof(KeepSubscription));

        // Takes the place of Delegate.Combine(Localization.OnLanguageChange, RefreshSearchTerm) in
        // Setup, whose result is stored back into the event. Combine copies the whole invocation
        // list, so in vanilla every Setup allocated an array as long as the ever-growing list.
        private static Delegate KeepSubscription(Delegate current, Delegate added) {
            if (ApiSwitch.Off || !Hooks.Healthy) { return Delegate.Combine(current, added); }

            return current;
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(nameof(BuildUiPieceButton.Setup))]
        private static IEnumerable<CodeInstruction> SetupTranspiler(IEnumerable<CodeInstruction> instructions) =>
            PatchHelper.ReplaceCalls(instructions, CombineMethod, KeepSubscriptionMethod, "BuildUiPieceButton.Setup", expected: 1);

        // Pooled buttons are skipped: Setup refreshes them before they are shown again.
        private static void OnLanguageChanged() {
            BuildUi buildUi = _buildUi;
            if (buildUi == null) { return; }

            foreach (BuildUiPieceButton button in buildUi.m_pieceButtons) {
                if (button != null) { button.RefreshSearchTerm(); }
            }

            if (buildUi.m_specialPieceButton != null) { buildUi.m_specialPieceButton.RefreshSearchTerm(); }
        }

        // Turned off, every button the menu holds is subscribed once, as Setup would have left it
        // but without the duplicates, and the handler is removed.
        private static FixSwitch CreateSwitch() {
            FixSwitch fixSwitch = FixRegistry.SwitchOf(typeof(BuildMenuLanguageEventPatch));
            fixSwitch.TurnedOff += () => {
                Localization.OnLanguageChange -= OnLanguageChanged;

                BuildUi buildUi = _buildUi;
                if (buildUi == null) { return; }

                foreach (BuildUiPieceButton button in buildUi.m_pieceButtons) { Subscribe(button); }
                foreach (BuildUiPieceButton button in buildUi.m_pieceButtonPool) { Subscribe(button); }
                Subscribe(buildUi.m_specialPieceButton);
            };
            return fixSwitch;
        }

        private static void Subscribe(BuildUiPieceButton button) {
            if (button != null) { Localization.OnLanguageChange += button.RefreshSearchTerm; }
        }

        [HarmonyPatch(typeof(BuildUi))]
        internal static class BuildUiHook {
            [HarmonyPostfix]
            [HarmonyPatch("Awake")]
            private static void AwakePostfix(BuildUi __instance) {
                _buildUi = __instance;
                if (ApiSwitch.Off) { return; }

                Localization.OnLanguageChange -= OnLanguageChanged;
                Localization.OnLanguageChange += OnLanguageChanged;
            }

            [HarmonyPostfix]
            [HarmonyPatch("OnDestroy")]
            private static void OnDestroyPostfix(BuildUi __instance) {
                if (!ReferenceEquals(_buildUi, __instance)) { return; }

                _buildUi = null;
                Localization.OnLanguageChange -= OnLanguageChanged;
            }
        }
    }
}
