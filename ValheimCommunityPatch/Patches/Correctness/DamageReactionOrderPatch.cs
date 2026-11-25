using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // SEMan.OnDamaged hands a hit to effects in the order applied, so an earlier shield can stop a reaction firing.
    // Prefixes OnDamaged to pass the hit to every SE_React first, then to the rest, each group in vanilla's order.
    // Reactions such as frost orbs see each hit before a shield absorbs it, whichever was applied first.
    // Provenance: contributed by searica (PR #22).
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(SEMan), nameof(SEMan.OnDamaged))]
    internal static class DamageReactionOrderPatch {
        private const string FixName = "Fix Damage Reaction Order";
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(DamageReactionOrderPatch),
                ValConfig.SectionCorrectness,
                FixName,
                true,
                "Status effects that strike back when you are hit, such as frost orbs, react to each hit " +
                "before a shield absorbs it, whichever of the two was applied first.");
        }

        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            typeof(DamageReactionOrderPatch),
            AccessTools.DeclaredMethod(typeof(SEMan), nameof(SEMan.OnDamaged)),
            HookKinds.Transpilers,
            owners => $"Status effect hit handling is changed by {owners}, so '{FixName}' stands down " +
                      "and that mod's version applies.");

        // A shield scales the hit's damage to zero, and a reaction fires only when the damage reaches its minimum.
        // Vanilla's frost orbs have no minimum, so the order shows with mods' reaction effects.
        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(SEMan __instance, HitData hit, Character attacker, bool __runOriginal) {
            if (!__runOriginal) { return false; }
            if (Enabled == null || !Enabled.Value || Takeover.TakenOver) { return true; }

            List<StatusEffect> effects = __instance.m_statusEffects;
            bool hasReaction = false;

            // foreach, as vanilla, so an effect that edits the list mid-hit fails the same way.
            foreach (StatusEffect statusEffect in effects) {
                if (statusEffect is SE_React) {
                    statusEffect.OnDamaged(hit, attacker);
                    hasReaction = true;
                }
            }

            if (!hasReaction) { return true; }

            // foreach, as vanilla, so an effect that edits the list mid-hit fails the same way.
            foreach (StatusEffect effect in effects) {
                if (!(effect is SE_React)) { effect.OnDamaged(hit, attacker); }
            }

            return false;
        }
    }
}
