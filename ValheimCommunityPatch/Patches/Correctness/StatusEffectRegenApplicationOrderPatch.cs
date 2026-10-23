using System;
using System.Collections.Generic;
using HarmonyLib;
using System.Reflection.Emit;

namespace ValheimCommunityPatch.Patches.Correctness
{
    [HarmonyPatch]
    [ModDisableable]
    [PatchSide(Side.Both)]
    internal static class StatusEffectRegenApplicationOrderPatch
    {
        private const string FixName = "Fix Status Effect Regen Application Order";

        internal static FixToggle Enabled;

        internal static void BindConfig()
        {
            Enabled = ValConfig.BindFixToggle(
                typeof(StatusEffectRegenApplicationOrderPatch),
                ValConfig.SectionCorrectness,
                FixName,
                true,
                "Regen bonuses and penalties from status effects are applied in a stable ordering so that the resulting " +
                "final regen multiplier no longer depends on the order the status effects were applied in.");
        }

        private static readonly TakeoverCheck HealthRegenTakeover = new TakeoverCheck(
            typeof(StatusEffectRegenApplicationOrderPatch),
            AccessTools.DeclaredMethod(typeof(SEMan), nameof(SEMan.ModifyHealthRegen)),
            HookKinds.Transpilers,
            owners => $"Modifying health regen is changed by {owners}, so '{FixName}' stands down " +
                      "and that mod's version applies.");

        private static readonly TakeoverCheck StaminaRegenTakeover = new TakeoverCheck(
            typeof(StatusEffectRegenApplicationOrderPatch),
            AccessTools.DeclaredMethod(typeof(SEMan), nameof(SEMan.ModifyStaminaRegen)),
            HookKinds.Transpilers,
            owners => $"Modifying stamina regen is changed by {owners}, so '{FixName}' stands down " +
                      "and that mod's version applies.");

        private static readonly TakeoverCheck EitrRegenTakeover = new TakeoverCheck(
            typeof(StatusEffectRegenApplicationOrderPatch),
            AccessTools.DeclaredMethod(typeof(SEMan), nameof(SEMan.ModifyEitrRegen)),
            HookKinds.Transpilers,
            owners => $"Modifying eitr regen is changed by {owners}, so '{FixName}' stands down " +
                      "and that mod's version applies.");

        /// <summary>
        ///     Applies the modifications caused by <see cref="SEMan.ModifyHealthRegen(ref float)"/>  
        ///     to health regen from all status effects using a stable ordering that does not depend
        ///     on the insertion order of the status effects.
        ///     First collects the total regen penalty (stacked multiplicatively) and the total regen 
        ///     bonus (stacked additively). Then, it applies the total regen penalty followed by the
        ///     total regen bonus. 
        /// </summary>
        /// <remarks>
        ///     Penatlies stack multiplicatively and bonuses stack additively in order to match the
        ///     stacking behavior of the base game. While the order of application for the total 
        ///     regen penalty and total regen bonus was chosen so that if a player has a single 
        ///     source of regen penalty at -X% and a single source of regen bonus at +X%, the two 
        ///     effects cancel out entirely. 
        /// </remarks>
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyHealthRegen))]
        private static bool StableModifyHealthRegen(SEMan seman, ref float regenMultiplier, bool __runOriginal)
        {
            if (!__runOriginal) { return false; }

            if (Enabled == null || !Enabled.Value || HealthRegenTakeover.TakenOver) { return true; }


            float testValue;
            float bonus = 0.0f;
            float penalty = 1.0f;

            // account for possible changes made to the regen multiplier by other mods prior
            // to the call to SEMan.ModifyHealthRegen
            if (regenMultiplier > 1.0f)
            {
                bonus += regenMultiplier - 1.0f;
            }
            else if (regenMultiplier < 1.0f)
            {
                penalty *= regenMultiplier;
            }

            // loop over all active status effects to get bonus and penalties to regen
            foreach (StatusEffect statusEfect in seman.m_statusEffects)
            {
                // call the status effect modify method to make sure we capture any 
                // possible changes by other mods and avoid accessing the raw multiplier 
                // field since it is possible that a status effect could have the multiplier
                // field set, but not implement the modify regen method and therefore should
                // not be included in the net regen bonus or penalty
                testValue = 1.0f;
                statusEfect.ModifyHealthRegen(ref testValue);
                if (testValue > 1.0f)
                {
                    bonus += testValue - 1.0f;
                }
                else if (testValue < 1.0f)
                {
                    penalty *= testValue;
                }
            }

            // update the regenMultiplier that was passed by refrence using the net penalties and bonuses
            // this ensure the resulting regenMultiplier does not depend on the order of the status effects
            regenMultiplier = (1.0f * penalty) + bonus;

            return false; // prevent vanilla method from running
        }

        /// <summary>
        ///     Applies the modifications caused by <see cref="SEMan.ModifyStaminaRegen(ref float)"/>  
        ///     to stamina regen from all status effects using a stable ordering that does not depend
        ///     on the insertion order of the status effects. 
        ///     First collects the total regen penalty (stacked multiplicatively) and the total regen 
        ///     bonus (stacked additively). Then, it applies the total regen penalty followed by the
        ///     total regen bonus. 
        /// </summary>
        /// <remarks>
        ///     Penatlies stack multiplicatively and bonuses stack additively in order to match the
        ///     stacking behavior of the base game. While the order of application for the total 
        ///     regen penalty and total regen bonus was chosen so that if a player has a single 
        ///     source of regen penalty at -X% and a single source of regen bonus at +X%, the two 
        ///     effects cancel out entirely. 
        /// </remarks>
        private static bool StableModifyStaminaRegen(SEMan seman, ref float regenMultiplier, bool __runOriginal)
        {
            if (!__runOriginal) { return false; }

            if (Enabled == null || !Enabled.Value || StaminaRegenTakeover.TakenOver) { return true; }

            float testValue;
            float bonus = 0.0f;
            float penalty = 1.0f;

            // account for possible changes made to the regen multiplier by other mods prior
            // to the call to SEMan.ModifyStaminaRegen
            if (regenMultiplier > 1.0f)
            {
                bonus += regenMultiplier - 1.0f;
            }
            else if (regenMultiplier < 1.0f)
            {
                penalty *= regenMultiplier;
            }

            // loop over all active status effects to get bonus and penalties to regen
            foreach (StatusEffect statusEfect in seman.m_statusEffects)
            {
                // call the status effect modify method to make sure we capture any 
                // possible changes by other mods and avoid accessing the raw multiplier 
                // field since it is possible that a status effect could have the multiplier
                // field set, but not implement the modify regen method and therefore should
                // not be included in the net regen bonus or penalty
                testValue = 1.0f;
                statusEfect.ModifyStaminaRegen(ref testValue);
                if (testValue > 1.0f)
                {
                    bonus += testValue - 1.0f;
                }
                else if (testValue < 1.0f)
                {
                    penalty *= testValue;
                }
            }

            // update the regenMultiplier that was passed by refrence using the net penalties and bonuses
            // this ensure the resulting regenMultiplier does not depend on the order of the status effects
            regenMultiplier = (1.0f * penalty) + bonus;
            //Logger.LogDebug($"Final stamina regen multiplier: {regenMultiplier}");

            return false;
        }


        /// <summary>
        ///     Applies the modifications caused by <see cref="SEMan.ModifyEitrRegen(ref float)"/>  
        ///     to eitr regen from all status effects using a stable ordering that does not depend
        ///     on the insertion order of the status effects.
        ///     First collects the total regen penalty (stacked multiplicatively) and the total regen 
        ///     bonus (stacked additively). Then, it applies the total regen penalty followed by the
        ///     total regen bonus. 
        /// </summary>
        /// <remarks>
        ///     Penatlies stack multiplicatively and bonuses stack additively in order to match the
        ///     stacking behavior of the base game. While the order of application for the total 
        ///     regen penalty and total regen bonus was chosen so that if a player has a single 
        ///     source of regen penalty at -X% and a single source of regen bonus at +X%, the two 
        ///     effects cancel out entirely. 
        /// </remarks>
        /// <param name="seman"></param>
        /// <param name="regenMultiplier"></param>
        private static bool StableModifyEitrRegen(SEMan seman, ref float regenMultiplier, bool __runOriginal)
        {
            if (!__runOriginal) { return false; }

            if (Enabled == null || !Enabled.Value || EitrRegenTakeover.TakenOver) { return true; }

            float testValue;
            float bonus = 0.0f;
            float penalty = 1.0f;

            // account for possible changes made to the regen multiplier by other mods prior
            // to the call to SEMan.ModifyEitrRegen
            if (regenMultiplier > 1.0f)
            {
                bonus += regenMultiplier - 1.0f;
            }
            else if (regenMultiplier < 1.0f)
            {
                penalty *= regenMultiplier;
            }

            // loop over all active status effects to get bonus and penalties to regen
            foreach (StatusEffect statusEfect in seman.m_statusEffects)
            {
                // call the status effect modify method to make sure we capture any 
                // possible changes by other mods and avoid accessing the raw multiplier 
                // field since it is possible that a status effect could have the multiplier
                // field set, but not implement the modify regen method and therefore should
                // not be included in the net regen bonus or penalty
                testValue = 1.0f;
                statusEfect.ModifyEitrRegen(ref testValue);
                if (testValue > 1.0f)
                {
                    bonus += testValue - 1.0f;
                }
                else if (testValue < 1.0f)
                {
                    penalty *= testValue;
                }
            }

            // update the regenMultiplier that was passed by refrence using the net penalties and bonuses
            // this ensure the resulting regenMultiplier does not depend on the order of the status effects
            regenMultiplier = (1.0f * penalty) + bonus;
            //Logger.LogDebug($"Final eitr regen multiplier: {regenMultiplier}");

            return false;
        }
    }
}
