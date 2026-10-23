using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Damage Reaction Order: status effects that strike back when their wearer is hit, such as
    // frost orbs, see each hit before other effects, such as a shield, change it.
    //
    // SEMan.OnDamaged hands the hit to each active effect in the order the effects were added. A
    // shield (SE_Shield) absorbs the hit by scaling its damage to zero, and a reaction (SE_React)
    // fires only when the hit's damage reaches its minimum, so whether a reaction with a minimum
    // fires depends on whether it or the shield was applied first. Vanilla's own frost orbs have no
    // minimum, so the difference shows with mods' reaction effects.
    //
    // A prefix passes the hit to every SE_React first and then to every other effect, each group in
    // vanilla's list order; with no reaction active the call is left to vanilla. It runs after other
    // mods' prefixes and respects one that already replaced the call, and stands down for the
    // session when another mod transpiles the method.
    //
    // Both: OnDamaged runs on whichever side owns the character. Provenance: contributed by
    // searica (PR #22).
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

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(SEMan __instance, HitData hit, Character attacker, bool __runOriginal) {
            if (!__runOriginal) { return false; }
            if (Enabled == null || !Enabled.Value || Takeover.TakenOver) { return true; }

            List<StatusEffect> effects = __instance.m_statusEffects;
            if (!HasReaction(effects)) { return true; }

            // foreach, as vanilla, so an effect that edits the list mid-hit fails the same way.
            foreach (StatusEffect effect in effects) {
                if (effect is SE_React) { effect.OnDamaged(hit, attacker); }
            }

            foreach (StatusEffect effect in effects) {
                if (!(effect is SE_React)) { effect.OnDamaged(hit, attacker); }
            }

            return false;
        }

        private static bool HasReaction(List<StatusEffect> effects) {
            for (int i = 0; i < effects.Count; i++) {
                if (effects[i] is SE_React) { return true; }
            }

            return false;
        }
    }
}
