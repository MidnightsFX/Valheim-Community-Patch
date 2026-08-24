using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Vanilla defect: Player.AddStamina bounds only the top of the range.
    //
    //   public override void AddStamina(float v)
    //   {
    //     this.m_stamina += v;
    //     if ((double) this.m_stamina <= (double) this.m_maxStamina)
    //       return;
    //     this.m_stamina = this.m_maxStamina;
    //   }
    //
    // Nothing stops m_stamina going below zero. Vanilla never passes a negative v itself, so this
    // only opens up under mods: anything that reduces stamina with AddStamina(-x) - rather than
    // UseStamina, whose RPC does floor at zero - takes the field arbitrarily negative.
    //
    // Player-visible symptom: every HaveStamina gate is a strict >, so while stamina is negative the
    // player cannot attack, run, dodge, sneak or build, jumps only weakly, takes drowning damage
    // while swimming, and cannot move at all if encumbered.
    //
    // A negative value is largely self-correcting, and the fix should not claim otherwise.
    // UpdateStats calls UpdateFood every frame, which once a second calls SetMaxStamina, which ends
    // in Mathf.Clamp(m_stamina, 0f, m_maxStamina) - so a negative is snapped back to zero within
    // about a second of normal play, and Player.Load clamps the same way, so it does not survive a
    // relog. What that leaves is a sub-second window in which the gates all read false, the value is
    // published to the ZDO for other peers, and the HUD bar draws negative.
    //
    // NaN is the case nothing recovers from. Unity's Mathf.Clamp is a pair of comparisons -
    // `if (value < min) ... else if (value > max) ...` - and every comparison against NaN is false,
    // so all three of vanilla's clamps return NaN unchanged. The regen gate m_stamina < maxStamina
    // is false as well, so regen never runs, HaveStamina is false forever, GetStaminaPercentage
    // feeds NaN to the HUD bar, and Player.Save writes the NaN straight back out to the character
    // file with no validation. A player in that state stays broken across every relog.
    //
    // NaN also has a network-facing route. RPC_UseStamina is a registered RPC - Player.Awake does
    // m_nview.Register<float>("UseStamina", ...) - so the float arrives from whichever peer sent it,
    // and the handler checks only v == 0:
    //
    //   private void RPC_UseStamina(long sender, float v)
    //   {
    //     if ((double) v == 0.0) return;
    //     this.m_stamina -= v;
    //     if ((double) this.m_stamina < 0.0) this.m_stamina = 0.0f;
    //     this.m_staminaRegenTimer = this.m_staminaRegenDelay;
    //   }
    //
    // The public UseStamina wrapper does screen NaN, but its guard runs one line too early:
    //
    //   if ((double) v == 0.0 || float.IsNaN(v)) return;
    //   v *= Game.m_staminaRate;
    //
    // Game.m_staminaRate is a public static float filled from the StaminaRate world key by
    // Game.trySetScalarKey, which parses with float.TryParse(s, NumberStyles.Any, InvariantCulture,
    // ...) - and that accepts the literal "NaN". So a single malformed global key makes every local
    // UseStamina call multiply a screened value straight back into NaN after the screen.
    //
    // Fix: floor the field at zero after the two writes that can breach it - Player.AddStamina and
    // Player.Load - so a character already saved in a bad state is repaired as it enters the world.
    // RPC_UseStamina is handled the other way round, by dropping a call whose v is not finite rather
    // than repairing m_stamina afterwards: repairing would mean a peer sending garbage silently
    // resets the victim's stamina to zero, where dropping leaves it untouched. That is the same
    // decision vanilla's own wrapper makes, applied where the wrapper does not reach.
    //
    // Scope note: the floor only. RPC_UseStamina with a finite negative v can push stamina above
    // m_maxStamina, but SetMaxStamina's per-second clamp already pulls that back, and imposing a
    // ceiling here would fight mods that grant over-max stamina on purpose. So as a *field value*
    // positive infinity passes untouched - it is above the floor - while negative infinity and NaN
    // are repaired, because neither is a usable number. As an *RPC input* both infinities are
    // rejected along with NaN: nothing legitimate sends one, and unlike the field there is no cost
    // to refusing it. A corrupt m_maxStamina is a different field with a different set of writers
    // and is left alone.
    //
    // Client: Player.Load is the local character's profile load, and AddStamina / RPC_UseStamina only
    // fire on the Player's owner, which is that player's own machine.
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

        // Keyed on player name rather than the Player object so nothing holds a reference to a
        // destroyed one. A name goes in when stamina is first found out of bounds and comes out once
        // it has genuinely recovered, so a mod that breaches the floor repeatedly logs once.
        private static readonly HashSet<string> Reported = new HashSet<string>();

        // Separate from Reported, and never cleared. A rejected RPC leaves stamina untouched, so
        // there is no recovery event to end an episode on, and sharing the one set would let a
        // stream of bad RPCs suppress a genuine floor warning - or the reverse.
        private static readonly HashSet<string> ReportedRpc = new HashSet<string>();

        private static void Repair(Player player, string site) {
            float stamina = player.m_stamina;

            // Ordered so the common case - a healthy positive value - costs one comparison. Positive
            // infinity lands here too, which is intended: it is above the floor, so it is not this
            // patch's business.
            if (stamina > 0f) {
                if (Reported.Count > 0) { Reported.Remove(player.GetPlayerName()); }
                return;
            }

            // Exactly zero is the value this patch itself writes, so it must not count as a recovery
            // - otherwise a mod breaching the floor every frame would clear and re-arm the log every
            // frame, which is the spam this is trying to avoid. NaN fails both comparisons and falls
            // through to the repair below, which is the point: it is the one value vanilla's own
            // clamps pass straight through.
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

        // Input guard rather than a repair: for any finite v vanilla's own floor already does the
        // right thing, so NaN and the infinities are the only values worth intercepting, and the
        // correct answer for those is to drop the call rather than let it land and then zero the
        // player's stamina putting it right.
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
