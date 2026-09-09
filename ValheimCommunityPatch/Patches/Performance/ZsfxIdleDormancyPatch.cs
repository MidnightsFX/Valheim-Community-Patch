using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Idle Sound Updates: a finished one-shot sound leaves the per-frame updater list until
    // something plays it again.
    //
    // ZSFX is an IMonoUpdater, so MonoUpdaters.Update walks ZSFX.Instances every frame. The list
    // is dominated by transient effect prefabs - well over a thousand prefabs carry a ZSFX, and
    // hits, footsteps, swings and pickups spawn them constantly - so a loaded scene holds several
    // hundred to a couple of thousand. Before CustomUpdate reaches any early-out it evaluates
    // IsLooping(), reading m_audioSource.loop, and then m_audioSource.isPlaying: two native
    // property reads across the managed boundary, per instance, per frame, for sounds that
    // finished playing long ago. MonoUpdatersExtra.CustomUpdate also copies the whole list into a
    // scratch list before iterating it, so the list's length costs a memcpy every frame too.
    //
    // A prefix cannot help, because deciding whether an instance is idle needs the same two native
    // reads. Instead a postfix on CustomUpdate - vanilla always runs, and the postfix decides
    // whether that was the last tick - removes provably idle instances from ZSFX.Instances and
    // records them. An instance is idle when it has an audio source that is not looping and not
    // playing, its play-on-awake delay has already fired, and it has no pending awake fade-out.
    // Those are read straight off vanilla's own control flow: with them all true, CustomUpdate
    // accumulates m_time, skips the delay branch, skips the concurrency ramp because the source
    // is not looping, and returns at "if (!m_audioSource.isPlaying) return". The only reader of
    // m_time left is the awake fade-out, which the last condition excludes, so freezing m_time is
    // unobservable. Nothing in ZSFX writes m_audioSource.loop at runtime, and m_delay is written
    // only by Awake and OnDisable.
    //
    // Waking is event-driven. Play is the only method that starts the source, so its postfix
    // re-adds; FadeOut is hooked as well so a mod that fades then replays cannot be stranded.
    // OnEnable and OnDisable both drop the record, because vanilla has just added or removed the
    // instance itself - which keeps the invariant this fix relies on, that a recorded instance is
    // never in ZSFX.Instances, so waking never has to scan the list. It also means a recorded
    // instance is always dropped while its object is still alive, since Unity fires OnDisable
    // before OnDestroy. The one path that bypasses all of this is another mod or an animation
    // event calling AudioSource.Play directly, so a watchdog on ZNetScene.Update, self-throttled
    // to once a second and skipped entirely while nothing is asleep, wakes any sleeper whose
    // source turns out to be playing. A woken sleeper can lose up to a second of volume and pitch
    // updates in that case, which is the cost of not paying two native reads per instance per
    // frame for the rest.
    //
    // The cost to weigh against that, and the reason this fix wants a measurement pass rather than
    // an argument: ZSFX.Instances is a List, so removing from it is a linear scan, and vanilla
    // OnDisable already pays one such scan for every instance. Sleeping an instance therefore adds
    // a second scan - the one at sleep time, plus vanilla OnDisable now scanning the whole list and
    // finding nothing. For a long-lived idle sound, one that sits in the list for minutes between
    // plays, that is paid once and saves two native reads a frame for the whole interval. For a
    // short-lived transient - a hit or a footstep destroyed a second after it finishes - the two
    // scans may cost more than the frames they save. Confirm against the sampler before trusting
    // this fix, and if transients dominate, the answer is to sleep only instances that have been
    // idle for some seconds rather than immediately.
    //
    // Client: ZSFX drives an AudioSource, and a dedicated server has no audio.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(ZSFX))]
    internal static class ZsfxIdleDormancyPatch {
        private const float WatchdogInterval = 1f;

        // Invariant: an instance in here is not in ZSFX.Instances. Keyed on the instance id, and
        // always dropped while the object is alive (OnDisable runs before OnDestroy).
        private static readonly Dictionary<int, ZSFX> Sleepers = new Dictionary<int, ZSFX>();
        private static readonly List<ZSFX> WatchdogWakes = new List<ZSFX>();
        private static float _nextWatchdog;

        private static void Wake(ZSFX sfx) {
            // Only a recorded instance is missing from the list, so this is the whole wake path -
            // no Contains scan over a list that runs into the thousands.
            if (Sleepers.Remove(sfx.GetInstanceID())) { ZSFX.Instances.Add(sfx); }
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(ZSFX.CustomUpdate))]
        private static void CustomUpdatePostfix(ZSFX __instance) {
            AudioSource source = __instance.m_audioSource;
            if (source == null) { return; }

            // Every branch of CustomUpdate above its isPlaying return is dead under these.
            if (source.loop || source.isPlaying) { return; }
            if (__instance.m_delay >= 0f) { return; }
            if (__instance.m_fadeOutOnAwake) { return; }

            if (!ZSFX.Instances.Remove(__instance)) { return; }
            Sleepers[__instance.GetInstanceID()] = __instance;
        }

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
            // animation event, calling AudioSource.Play directly.
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
