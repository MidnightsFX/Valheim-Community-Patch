using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // SEMan applies regen effects in the order added, so a penalty scales only the bonuses applied before it.
    // Prefixes SEMan.ModifyHealthRegen, ModifyStaminaRegen and ModifyEitrRegen to apply all bonuses, then penalties.
    // Health, stamina and eitr regen add up the same whichever order the status effects were applied in.
    // Provenance: contributed by searica (PR #25).
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class StatusEffectRegenApplicationOrderPatch {
        private const string FixName = "Fix Status Effect Regen Application Order";
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(StatusEffectRegenApplicationOrderPatch),
                ValConfig.SectionCorrectness,
                FixName,
                true,
                "Makes regen bonuses and penalties from status effects add up the same in any order, so Wet " +
                "or Freezing always reduce the extra regen from Rested.");
        }

        private enum Regen { Health, Stamina, Eitr }

        private static readonly TakeoverCheck HealthTakeover = TakeoverOf(nameof(SEMan.ModifyHealthRegen), "Health");
        private static readonly TakeoverCheck StaminaTakeover = TakeoverOf(nameof(SEMan.ModifyStaminaRegen), "Stamina");
        private static readonly TakeoverCheck EitrTakeover = TakeoverOf(nameof(SEMan.ModifyEitrRegen), "Eitr");

        private static TakeoverCheck TakeoverOf(string method, string stat) => new TakeoverCheck(
            typeof(StatusEffectRegenApplicationOrderPatch),
            AccessTools.DeclaredMethod(typeof(SEMan), method),
            HookKinds.Transpilers,
            owners => $"{stat} regen from status effects is changed by {owners}, so '{FixName}' stands " +
                      "down for it and that mod's version applies.");

        // Priority.Last on all three: see ValheimCommunityPatch.ApplyPatches. The argument names
        // match vanilla's, which differ per method.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyHealthRegen))]
        private static bool HealthRegenPrefix(SEMan __instance, ref float regenMultiplier, bool __runOriginal) =>
            Apply(__instance, ref regenMultiplier, Regen.Health, HealthTakeover, __runOriginal);

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyStaminaRegen))]
        private static bool StaminaRegenPrefix(SEMan __instance, ref float staminaMultiplier, bool __runOriginal) =>
            Apply(__instance, ref staminaMultiplier, Regen.Stamina, StaminaTakeover, __runOriginal);

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyEitrRegen))]
        private static bool EitrRegenPrefix(SEMan __instance, ref float eitrMultiplier, bool __runOriginal) =>
            Apply(__instance, ref eitrMultiplier, Regen.Eitr, EitrTakeover, __runOriginal);

        private static bool Apply(SEMan seman, ref float multiplier, Regen regen, TakeoverCheck takeover, bool runOriginal) {
            if (!runOriginal) { return false; }
            if (Enabled == null || !Enabled.Value || takeover.TakenOver) { return true; }

            // Pass 0 bonuses, pass 1 penalties and effects that leave regen alone, each in vanilla's order.
            // An effect's group is what it does to a multiplier of 1, so mods' effects sort by their own
            // overrides. foreach, as vanilla, so an effect that edits the list mid-call fails the same way.
            List<StatusEffect> effects = seman.m_statusEffects;
            for (int pass = 0; pass < 2; pass++) {
                foreach (StatusEffect effect in effects) {
                    float alone = 1f;
                    Modify(effect, regen, ref alone);
                    if ((alone > 1f) == (pass == 0)) { Modify(effect, regen, ref multiplier); }
                }
            }

            return false;
        }

        private static void Modify(StatusEffect effect, Regen regen, ref float multiplier) {
            switch (regen) {
                case Regen.Health: effect.ModifyHealthRegen(ref multiplier); break;
                case Regen.Stamina: effect.ModifyStaminaRegen(ref multiplier); break;
                default: effect.ModifyEitrRegen(ref multiplier); break;
            }
        }
    }
}
