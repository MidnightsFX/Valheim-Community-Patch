using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Negative Stamina: floors player stamina at zero and refuses a NaN or infinite stamina
    // drain from the network.
    //
    // Player.AddStamina clamps only the top of the range, so a mod calling AddStamina with a
    // negative value can take stamina below zero, where every HaveStamina gate fails: no attack,
    // run, dodge or build. Vanilla's per-second SetMaxStamina clamp repairs a plain negative, but
    // NaN passes every clamp (all comparisons against NaN are false), stops regeneration for good
    // and is written to the character file on save, so it survives relogging. NaN can also arrive
    // through RPC_UseStamina, whose handler screens nothing, and through the public UseStamina
    // wrapper, whose NaN check runs before it multiplies by the StaminaRate world key, which can
    // itself parse as NaN.
    //
    // Postfixes on AddStamina and Player.Load floor m_stamina at zero (NaN included), repairing a
    // character that loads in broken. A prefix on RPC_UseStamina drops a call whose value is NaN
    // or infinite rather than applying it and then zeroing the victim's stamina. Positive infinity
    // as a field value is left alone: it is above the floor, and a ceiling would fight mods that
    // grant over-max stamina on purpose.
    //
    // Client: Player.Load, AddStamina and RPC_UseStamina all run on the player's own machine.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(Player))]
    internal static class NegativeStaminaPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(NegativeStaminaPatch),
                ValConfig.SectionCorrectness,
                "Fix Negative Stamina",
                true,
                "Floors player stamina at zero after Player.AddStamina, which bounds only the top of " +
                "the range, and repairs a character that loads in already broken. Also drops a " +
                "UseStamina network message carrying NaN or infinity, which vanilla would apply and " +
                "leave your stamina permanently stuck. Only reachable when another mod reduces " +
                "stamina without checking the value first.");
        }

        // Keyed on player name so nothing holds a destroyed Player. A name is added when stamina is
        // first found out of bounds and removed once it recovers, so a repeat offender logs once.
        private static readonly HashSet<string> Reported = new HashSet<string>();

        // Separate and never cleared: a rejected RPC leaves stamina untouched, so there is no
        // recovery event to end an episode on.
        private static readonly HashSet<string> ReportedRpc = new HashSet<string>();

        private static void Repair(Player player, string site) {
            float stamina = player.m_stamina;

            if (stamina > 0f) {
                if (Reported.Count > 0) { Reported.Remove(player.GetPlayerName()); }
                return;
            }

            // Exactly zero is what this patch writes, so it must not count as a recovery or a mod
            // breaching the floor every frame would re-arm the log every frame. NaN fails both
            // comparisons and falls through to the repair, which is the point.
            if (stamina == 0f) { return; }

            player.m_stamina = 0f;

            if (Reported.Add(player.GetPlayerName())) {
                Logger.LogWarning(
                    $"Player stamina was {stamina} after {site}; floored to 0. Another mod is " +
                    "reducing stamina without checking the value first. Logged once until it " +
                    "recovers.");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(Player.AddStamina))]
        private static void AddStaminaPostfix(Player __instance) {
            if (Enabled == null || !Enabled.Value) { return; }

            Repair(__instance, "AddStamina");
        }

        [HarmonyPrefix]
        [HarmonyPatch("RPC_UseStamina")]
        private static bool RpcUseStaminaPrefix(Player __instance, float v) {
            if (Enabled == null || !Enabled.Value) { return true; }
            if (!float.IsNaN(v) && !float.IsInfinity(v)) { return true; }

            if (ReportedRpc.Add(__instance.GetPlayerName())) {
                Logger.LogWarning(
                    $"Player.RPC_UseStamina was called with {v}; ignoring it. Vanilla would have " +
                    "made this player's stamina permanently NaN. Logged once per player.");
            }

            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(Player.Load))]
        private static void LoadPostfix(Player __instance) {
            if (Enabled == null || !Enabled.Value) { return; }

            Repair(__instance, "Load");
        }
    }
}
