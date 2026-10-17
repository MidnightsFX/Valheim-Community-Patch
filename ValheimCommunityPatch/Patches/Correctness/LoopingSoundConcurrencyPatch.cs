using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Looping Sound Muting: a looping sound that is switched off and on again, such as a beehive
    // at dawn or a fermenter's second batch, keeps playing instead of going silent until its zone
    // reloads, and unrelated sounds such as a bonfire and a spinning wheel no longer silence each other.
    //
    // AudioMan.RegisterLoopingSound adds a looping ZSFX to m_loopingSfx each time it starts playing,
    // without checking whether it is already there, and only its destruction removes it. ZSFX.OnDisable
    // re-arms play-on-awake loops, so every off/on cycle adds the same sound again. Every 16 frames
    // UpdateLoopingConcurrency sorts all entries that share a hash by distance to the camera, keeps the
    // nearest ones audible and mutes the rest. A copy sorts right after its original and mutes it, and a
    // sound with a limit of 0 has only the one slot, so it goes silent. Switched-off sounds are ranked
    // too, so an unlit torch or cold cauldron nearby can take the slot from one that is playing. Some
    // prefabs never had their hash generated, so every sound left at hash 0 counts as one sound and only
    // the nearest of them plays.
    //
    // A prefix on RegisterLoopingSound skips a sound that is already registered, so each keeps one entry
    // and one destroy handler, and gives a hash-0 sound a hash made from its clip names, so instances of
    // one sound still share a limit. A transpiler swaps the one Utils.InsertSortNoAlloc call in the
    // ranking for a helper that leaves out switched-off sounds and repeated entries, then inserts as
    // vanilla does. Vanilla's limits are kept exactly. The filter is isActiveAndEnabled, never isPlaying:
    // VPO stops muted loops and restarts them only once they are ranked audible again. Turned off, the
    // given hashes go back to 0.
    //
    // Client: a dedicated server plays no sound, and the ranking returns early without a camera.
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
                "stop and start again. Vanilla registers the sound again on every restart, and the copy mutes " +
                "the original until the area reloads. Sounds that are switched off no longer take the place " +
                "of ones that are playing, and unrelated sounds such as a bonfire and a spinning wheel no " +
                "longer silence each other.");

            Enabled.SettingChanged += (sender, args) => {
                if (Enabled.Value) { RehashRegistered(); } else { RestoreHashes(); }
            };
        }

        private static readonly MethodInfo InsertSortMethod =
            AccessTools.Method(typeof(Utils), nameof(Utils.InsertSortNoAlloc))?.MakeGenericMethod(typeof(ZSFX));
        private static readonly MethodInfo InsertAudibleMethod =
            AccessTools.Method(typeof(LoopingSoundConcurrencyPatch), nameof(InsertAudible));

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
        // A skipped sound keeps its last audible or muted state until it is ranked again.
        private static void InsertAudible(List<ZSFX> same, ZSFX sfx, List<float> distances, float distance) {
            if (Enabled != null && Enabled.Value) {
                if (!sfx.isActiveAndEnabled) { return; }

                for (int i = 0; i < same.Count; i++) {
                    if (ReferenceEquals(same[i], sfx)) { return; }
                }
            }

            Utils.InsertSortNoAlloc(same, sfx, distances, distance);
        }

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
