using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Idle Sound Updates: finished one-shot sounds stay in the per-frame updater list at two native reads each.
    // Postfixes ZSFX.CustomUpdate to drop provably idle sounds from ZSFX.Instances; Play and FadeOut re-add them.
    // The per-frame audio update skips sounds that have finished playing.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZSFX))]
    internal static class ZsfxIdleDormancyPatch {
        private const float WatchdogInterval = 1f;

        // Invariant: an instance in here is not in ZSFX.Instances. Keyed on the instance id, and
        // always dropped while the object is alive (OnDisable runs before OnDestroy).
        private static readonly Dictionary<int, ZSFX> Sleepers = new Dictionary<int, ZSFX>();
        private static readonly List<ZSFX> WatchdogWakes = new List<ZSFX>();
        private static float _nextWatchdog;

        // Turned off, every sleeper goes back to the game's updater list.
        private static readonly FixSwitch ApiSwitch = CreateSwitch();

        private static FixSwitch CreateSwitch() {
            FixSwitch fixSwitch = FixRegistry.SwitchOf(typeof(ZsfxIdleDormancyPatch));
            fixSwitch.TurnedOff += RestoreAll;
            return fixSwitch;
        }

        private static void Wake(ZSFX sfx) {
            // Only a recorded instance is missing from the list, so this is the whole wake path -
            // no Contains scan over a list that runs into the thousands.
            if (Sleepers.Remove(sfx.GetInstanceID())) { ZSFX.Instances.Add(sfx); }
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(ZSFX.CustomUpdate))]
        private static void CustomUpdatePostfix(ZSFX __instance) {
            if (ApiSwitch.Off) { return; }

            AudioSource source = __instance.m_audioSource;
            if (source == null) { return; }

            // Every branch of CustomUpdate above its isPlaying return is dead under these. m_time is then read
            // only by the awake fade-out, excluded below, so freezing it is unobservable; nothing in ZSFX writes
            // loop at runtime, and m_delay is written only by Awake and OnDisable.
            if (source.loop || source.isPlaying) { return; }
            if (__instance.m_delay >= 0f) { return; }
            if (__instance.m_fadeOutOnAwake) { return; }

            // A List removal is a linear scan, and vanilla's OnDisable later scans again and finds nothing,
            // so a sound destroyed soon after it finishes can cost more to sleep than it saves.
            if (!ZSFX.Instances.Remove(__instance)) { return; }
            Sleepers[__instance.GetInstanceID()] = __instance;
        }

        // Play is the only method that starts the source. FadeOut wakes too, so a mod that fades then
        // replays cannot be stranded.
        [HarmonyPostfix]
        [HarmonyPatch(nameof(ZSFX.Play))]
        private static void PlayPostfix(ZSFX __instance) => Wake(__instance);

        [HarmonyPostfix]
        [HarmonyPatch(nameof(ZSFX.FadeOut))]
        private static void FadeOutPostfix(ZSFX __instance) => Wake(__instance);

        // Vanilla has just re-added this instance itself.
        [HarmonyPostfix]
        [HarmonyPatch("OnEnable")]
        private static void OnEnablePostfix(ZSFX __instance) => Sleepers.Remove(__instance.GetInstanceID());

        // Vanilla has just removed it, and this is the last moment the object is reliably alive.
        [HarmonyPostfix]
        [HarmonyPatch("OnDisable")]
        private static void OnDisablePostfix(ZSFX __instance) => Sleepers.Remove(__instance.GetInstanceID());

        [HarmonyPatch(typeof(ZNetScene))]
        internal static class SceneHooks {
            // Catches a source started without going through ZSFX.Play - another mod, or an
            // animation event, calling AudioSource.Play directly. Once a second, so such a sound
            // can miss up to a second of volume and pitch updates.
            [HarmonyPostfix]
            [HarmonyPatch("Update")]
            private static void UpdatePostfix() {
                if (Sleepers.Count == 0) { return; }

                float now = Time.unscaledTime;
                if (now < _nextWatchdog) { return; }
                _nextWatchdog = now + WatchdogInterval;

                foreach (KeyValuePair<int, ZSFX> entry in Sleepers) {
                    ZSFX sfx = entry.Value;
                    if (sfx == null) { continue; }

                    AudioSource source = sfx.m_audioSource;
                    if (source != null && source.isPlaying) { WatchdogWakes.Add(sfx); }
                }

                // Woken outside the enumeration; Wake mutates Sleepers.
                for (int i = 0; i < WatchdogWakes.Count; i++) { Wake(WatchdogWakes[i]); }
                WatchdogWakes.Clear();
            }

            [HarmonyPostfix]
            [HarmonyPatch("Shutdown")]
            private static void ShutdownPostfix() {
                Sleepers.Clear();
                WatchdogWakes.Clear();
                _nextWatchdog = 0f;
            }
        }

        /// <summary>
        /// Returns every sleeper to the vanilla updater list. Called when the mod unpatches itself,
        /// so an unpatched session is not left with silently frozen sounds.
        /// </summary>
        internal static void RestoreAll() {
            foreach (KeyValuePair<int, ZSFX> entry in Sleepers) {
                ZSFX sfx = entry.Value;
                if (sfx != null) { ZSFX.Instances.Add(sfx); }
            }

            Sleepers.Clear();
            WatchdogWakes.Clear();
        }
    }
}
