using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // AudioMan registers a looping sound again on each restart, and the copy mutes the original until its zone reloads.
    // Prefixes RegisterLoopingSound to drop repeats and hash unhashed sounds; a transpiler ranks only enabled ones.
    // Beehives, fermenters and workshop sounds keep playing, and unrelated sounds no longer silence each other.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(AudioMan))]
    internal static class LoopingSoundConcurrencyPatch {
        internal static FixToggle Enabled;

        // The hash-0 sounds given a hash, so turning the fix off can put 0 back. A sound leaves when it
        // is destroyed, as it leaves AudioMan's list.
        private static readonly HashSet<ZSFX> Rehashed = new HashSet<ZSFX>();
        private static readonly Action<ZSFX> ForgetRehashed = sfx => Rehashed.Remove(sfx);

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(LoopingSoundConcurrencyPatch),
                ValConfig.SectionCorrectness,
                "Fix Looping Sound Muting",
                true,
                "Keeps looping sounds such as beehives, fermenters and spinning wheels playing after they " +
                "stop and start again, and stops switched-off or unrelated sounds from muting them.");

            Enabled.SettingChanged += (sender, args) => {
                if (Enabled.Value) { RehashRegistered(); } else { RestoreHashes(); }
            };
        }

        private static readonly MethodInfo InsertSortMethod =
            AccessTools.Method(typeof(Utils), nameof(Utils.InsertSortNoAlloc))?.MakeGenericMethod(typeof(ZSFX));
        private static readonly MethodInfo InsertAudibleMethod =
            AccessTools.Method(typeof(LoopingSoundConcurrencyPatch), nameof(InsertAudible));

        // Vanilla adds the sound on every start without checking, and ZSFX.OnDisable re-arms play-on-awake
        // loops, so each off/on cycle added a copy that ranks right after the original and mutes it.
        [HarmonyPrefix]
        [HarmonyPatch("RegisterLoopingSound")]
        private static bool RegisterLoopingSoundPrefix(AudioMan __instance, ZSFX sfx) {
            if (Enabled == null || !Enabled.Value) { return true; }

            if (sfx.m_hash == 0) { Rehash(sfx); }

            return !__instance.m_loopingSfx.Contains(sfx);
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("UpdateLoopingConcurrency")]
        private static IEnumerable<CodeInstruction> UpdateLoopingConcurrencyTranspiler(IEnumerable<CodeInstruction> instructions) =>
            PatchHelper.ReplaceCalls(instructions, InsertSortMethod, InsertAudibleMethod, "AudioMan.UpdateLoopingConcurrency", expected: 1);

        // Takes the place of Utils.InsertSortNoAlloc(m_tmpSameSfx, sfx, m_tmpSameSfxDistance, distance).
        // A skipped sound keeps its last audible or muted state until it is ranked again. The filter is
        // isActiveAndEnabled, never isPlaying: VPO stops muted loops and restarts them only once ranked audible.
        private static void InsertAudible(List<ZSFX> same, ZSFX sfx, List<float> distances, float distance) {
            if (Enabled != null && Enabled.Value) {
                if (!sfx.isActiveAndEnabled) { return; }

                for (int i = 0; i < same.Count; i++) {
                    if (ReferenceEquals(same[i], sfx)) { return; }
                }
            }

            Utils.InsertSortNoAlloc(same, sfx, distances, distance);
        }

        // Some prefabs never had their hash generated, and vanilla limits every hash-0 sound as one sound.
        // A sum, so instances share the hash whatever order their clips are listed in. Never 0.
        private static void Rehash(ZSFX sfx) {
            int hash = 0;
            foreach (AudioClip clip in sfx.m_audioClips) {
                if (clip != null) { hash = unchecked(hash + clip.name.GetStableHashCode()); }
            }

            sfx.m_hash = hash != 0 ? hash : 1;
            if (Rehashed.Add(sfx)) { sfx.OnDestroyingSfx += ForgetRehashed; }
        }

        // Turned on mid-session: sounds registered while it was off are split at once.
        private static void RehashRegistered() {
            AudioMan audioMan = AudioMan.instance;
            if (audioMan == null) { return; }

            foreach (ZSFX sfx in audioMan.m_loopingSfx) {
                if (sfx != null && sfx.m_hash == 0) { Rehash(sfx); }
            }
        }

        private static void RestoreHashes() {
            foreach (ZSFX sfx in Rehashed) {
                if (sfx == null) { continue; }

                sfx.m_hash = 0;
                sfx.OnDestroyingSfx -= ForgetRehashed;
            }

            Rehashed.Clear();
        }
    }
}
