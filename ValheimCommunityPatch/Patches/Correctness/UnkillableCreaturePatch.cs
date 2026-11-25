using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // A creature whose death fails to complete stays at zero health for good, and NaN health makes one unkillable too.
    // Finalizes OnDeath, prefixes SetHealth against NaN, and a ZDOMan.Update scan finishes stuck deaths and NaN health.
    // Stuck creatures die properly instead of ignoring every hit, mostly Deep North ones whose owner changed mid-death.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(Character))]
    internal static class UnkillableCreaturePatch {
        internal static FixToggle Enabled;

        private const string FixName = "Fix Unkillable Creatures";

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(UnkillableCreaturePatch),
                ValConfig.SectionCorrectness,
                FixName,
                true,
                "Finishes the death of a creature left alive at zero health, and puts a creature with NaN " +
                "health back to full, instead of leaving either one unkillable.");
        }

        private const float ScanIntervalSeconds = 1f;

        // Vanilla calls CheckDeath every physics step, so a second at zero is not a death in progress.
        private const float CheckDeathGraceSeconds = 1f;

        // Past the latest Die event in any vanilla death clip, 10.2 s into the Frozen King's last one.
        private const float DeathAnimationGraceSeconds = 20f;

        private static readonly HookHealth Hooks = new HookHealth(
            typeof(UnkillableCreaturePatch),
            FixName,
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(Character), nameof(Character.OnDeath)), typeof(UnkillableCreaturePatch)));

        private enum Repair { NaNHealth, CheckDeath, OnDeath }

        private struct Pending {
            internal Character Character;
            internal Repair Repair;
        }

        private struct Sighting {
            internal float Since;
            internal int Scan;
        }

        private static float _nextScan;
        private static int _scan;

        // Owned creatures at zero health, by when this peer first saw them there. An entry not
        // refreshed by the latest scan is dropped, so a creature that changes hands starts again.
        private static readonly Dictionary<Character, Sighting> Seen = new Dictionary<Character, Sighting>();

        // Creatures the scan must not touch again: their OnDeath already ran on this peer.
        private static readonly HashSet<Character> Abandoned = new HashSet<Character>();

        private static readonly List<Pending> Queue = new List<Pending>();
        private static readonly List<Character> Stale = new List<Character>();
        private static readonly Predicate<Character> IsGone = character => character == null;

        // Keyed on prefab and cause, so a creature type that keeps failing the same way logs once.
        private static readonly HashSet<string> Reported = new HashSet<string>();

        private static void Report(Character character, string what) {
            string prefab = Utils.GetPrefabName(character.gameObject);
            if (Reported.Add(prefab + "|" + what)) {
                Logger.LogWarning($"{prefab} {what}. Logged once per creature type.");
            } else if (Logger.DebugEnabled) {
                Logger.LogDebug($"{prefab} at {character.transform.position} {what}.");
            }
        }

        private static bool IsOwnedCreature(Character character) {
            if (character == null || character.IsPlayer()) { return false; }

            ZNetView nview = character.m_nview;
            return nview != null && nview.IsValid() && nview.IsOwner();
        }

        // Player.OnDeath overrides this without calling it, so only creatures get here.
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(Character.OnDeath))]
        private static void OnDeathFinalizer(Character __instance, Exception __exception) {
            if (Enabled == null || !Enabled.Value) { return; }

            // A completed death has already detached the view through ZNetScene.Destroy.
            if (!IsOwnedCreature(__instance)) { return; }

            if (__exception == null) {
                // Returned with the creature still registered: another mod's prefix cancelled it.
                Abandoned.Add(__instance);
                return;
            }

            // Not run again: drops and kill credit may already have happened. The exception still
            // propagates, so its stack trace reaches the log for whoever threw it.
            Report(__instance,
                $"threw while dying ({__exception.GetType().Name}: {__exception.Message}), which would " +
                "have left it at zero health and unkillable; removed it, and some of its drops may be missing");
            ZNetScene.instance.Destroy(__instance.gameObject);
        }

        // NaN health fails every "at or below zero" test, so no hit kills it and no heal repairs it.
        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(nameof(Character.SetHealth))]
        private static bool SetHealthPrefix(Character __instance, float health, bool __runOriginal) {
            if (!__runOriginal) { return false; }
            if (!float.IsNaN(health)) { return true; }
            if (Enabled == null || !Enabled.Value || __instance.IsPlayer()) { return true; }

            Report(__instance,
                "was given NaN health by another mod's damage or healing, which would have made it " +
                "unkillable; kept its health as it was");
            return false;
        }

        // RPC_Damage and RPC_Heal return early at zero health, so a death is never retried once it fails:
        // when ownership moves mid-animation (the Die event fires only on the old owner), when OnDeath
        // throws, or when an earlier throw in the owner's update skips CheckDeath every tick.
        private static void Scan() {
            float now = Time.unscaledTime;
            if (now < _nextScan) { return; }
            _nextScan = now + ScanIntervalSeconds;

            if (ReferenceEquals(ZNetScene.instance, null) || !Hooks.Healthy) { return; }

            // Game time for the grace periods: a death animation stands still while the game is paused.
            float time = Time.time;
            _scan++;

            List<Character> characters = Character.GetAllCharacters();
            for (int i = 0; i < characters.Count; i++) {
                Character character = characters[i];
                if (!IsOwnedCreature(character) || !character.isActiveAndEnabled) { continue; }
                if (Abandoned.Contains(character)) { continue; }

                float health = character.GetHealth();
                if (float.IsNaN(health)) {
                    Queue.Add(new Pending { Character = character, Repair = Repair.NaNHealth });
                    continue;
                }

                if (!(health <= 0f)) { continue; }

                if (!Seen.TryGetValue(character, out Sighting seen)) {
                    Seen[character] = new Sighting { Since = time, Scan = _scan };
                    continue;
                }

                seen.Scan = _scan;
                Seen[character] = seen;

                float elapsed = time - seen.Since;
                if (!character.IsDead()) {
                    if (elapsed >= CheckDeathGraceSeconds) {
                        Queue.Add(new Pending { Character = character, Repair = Repair.CheckDeath });
                    }
                } else if (elapsed >= character.m_deathAnimationStartDelay + DeathAnimationGraceSeconds) {
                    Queue.Add(new Pending { Character = character, Repair = Repair.OnDeath });
                }
            }

            if (Seen.Count > 0) {
                foreach (KeyValuePair<Character, Sighting> entry in Seen) {
                    if (entry.Value.Scan != _scan) { Stale.Add(entry.Key); }
                }

                for (int i = 0; i < Stale.Count; i++) { Seen.Remove(Stale[i]); }
                Stale.Clear();
            }

            if (Abandoned.Count > 0) { Abandoned.RemoveWhere(IsGone); }

            // Applied after the loop: a death can spawn creatures (a boss's next phase, a mod's
            // splits), which adds to the list being walked.
            for (int i = 0; i < Queue.Count; i++) { Apply(Queue[i]); }
            Queue.Clear();
        }

        private static void Apply(Pending pending) {
            Character character = pending.Character;

            // An earlier repair's side effects may have removed it or handed it on.
            if (!IsOwnedCreature(character)) { return; }

            try {
                switch (pending.Repair) {
                    case Repair.NaNHealth:
                        RepairHealth(character);
                        break;

                    case Repair.CheckDeath:
                        Report(character,
                            "sat at zero health without its death check running, because something " +
                            "earlier in its update is failing; finished its death");
                        character.CheckDeath();
                        break;

                    case Repair.OnDeath:
                        // Before the call, so a death that fails again is not retried every second.
                        Abandoned.Add(character);
                        Report(character,
                            "was still dying long after its death animation should have finished, " +
                            "most likely because its owner changed partway through; finished its death");
                        character.OnDeath();
                        break;
                }
            } catch (Exception ex) {
                // OnDeath's own failures are reported by the finalizer.
                if (pending.Repair == Repair.NaNHealth) {
                    Logger.LogWarning($"{FixName}: could not repair {Utils.GetPrefabName(character.gameObject)}'s health: {ex.Message}");
                } else if (Logger.DebugEnabled) {
                    Logger.LogDebug($"{FixName}: finishing {Utils.GetPrefabName(character.gameObject)}'s death threw: {ex.Message}");
                }
            }
        }

        // Back to max health, as StarLevelSystem does.
        private static void RepairHealth(Character character) {
            // Max health is a ZDO value too, so it can be broken the same way. SetupMaxHealth is
            // vanilla's base-times-level; its clamp compares against the NaN health, which is false.
            float max = character.GetMaxHealth();
            if (!IsUsable(max)) {
                character.SetupMaxHealth();
                max = character.GetMaxHealth();
            }

            if (!IsUsable(max)) {
                Abandoned.Add(character);
                Report(character, "has NaN health and no usable max health to restore it to; left it alone");
                return;
            }

            character.SetHealth(max);
            Report(character, "had NaN health, which made it unkillable; reset it to max health");
        }

        private static bool IsUsable(float health) => health > 0f && !float.IsInfinity(health);

        // Every frame on every peer with a world loaded, and outside the physics update whose
        // failures it has to recover from.
        [HarmonyPatch(typeof(ZDOMan))]
        internal static class ScanHook {
            [HarmonyPostfix]
            [HarmonyPatch(nameof(ZDOMan.Update))]
            private static void UpdatePostfix() {
                if (Enabled == null || !Enabled.Value) { return; }

                Scan();
            }
        }
    }
}
