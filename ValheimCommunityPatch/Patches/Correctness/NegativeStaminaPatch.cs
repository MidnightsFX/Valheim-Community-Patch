using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Player.AddStamina clamps only the top, so a mod's drain can leave stamina negative or NaN, blocking every action.
    // Postfixes AddStamina and Player.Load to floor stamina at zero; prefixes RPC_UseStamina to drop NaN or infinity.
    // Players can always attack, run, dodge and build again, and a character saved with broken stamina loads repaired.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Player))]
    internal static class NegativeStaminaPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(NegativeStaminaPatch),
                ValConfig.SectionCorrectness,
                "Fix Negative Stamina",
                true,
                "Stops another mod's bad stamina change from leaving your stamina below zero or permanently " +
                "stuck, and repairs a character that loads in that way.");
        }

        // Keyed on player name so nothing holds a destroyed Player. A name is added when stamina is
        // first found out of bounds and removed once it recovers, so a repeat offender logs once.
        private static readonly HashSet<string> Reported = new HashSet<string>();

        // Separate and never cleared: a rejected RPC leaves stamina untouched, so there is no
        // recovery event to end an episode on.
        private static readonly HashSet<string> ReportedRpc = new HashSet<string>();

        // Vanilla's per-second clamp repairs a plain negative, but NaN passes every clamp, stops regeneration
        // for good and is saved with the character. Positive infinity is left alone: it is above the floor,
        // and a ceiling would fight mods that grant over-max stamina on purpose.
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

        // Vanilla's handler screens nothing, and the public UseStamina checks for NaN before multiplying by the
        // StaminaRate world key, which can itself parse as NaN. Dropped rather than applied and then floored.
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
