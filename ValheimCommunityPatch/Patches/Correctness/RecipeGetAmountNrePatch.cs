using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Vanilla defect: Recipe.GetAmount dereferences the result of GetFirstRequiredItem without a null
    // check, but that method returns null when the player holds none of the accepted ingredients:
    //
    //   if (this.m_requireOnlyOneIngredient) {
    //     singleReqItem = Player.m_localPlayer.GetFirstRequiredItem(...);
    //     amount += (int)Mathf.Ceil((float)((singleReqItem.m_quality - 1) * this.m_amount) * ...) + extraAmount;
    //   }
    //
    // Player-visible symptom: opening the crafting or upgrade panel for a "requires any one of these"
    // recipe while holding none of the ingredients throws a NullReferenceException, which leaves the
    // panel blank or frozen and spams the log.
    //
    // Fix: substitute a quality of 1 - the value a nonexistent item would contribute - when the lookup
    // came back null. The rest of the calculation is untouched, so a recipe that *does* find an
    // ingredient behaves exactly as before.
    //
    // Client: Recipe.GetAmount dereferences Player.m_localPlayer directly, so it cannot run headless.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(Recipe))]
    internal static class RecipeGetAmountNrePatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(RecipeGetAmountNrePatch),
                ValConfig.SectionCorrectness,
                "Fix Recipe Amount Crash",
                true,
                "Guards the null dereference in Recipe.GetAmount that throws when a 'requires any one of " +
                "these' recipe is displayed while you carry none of the accepted ingredients. Without it " +
                "the crafting/upgrade panel breaks. Changing this requires a game restart.");
        }

        private static readonly FieldInfo QualityField = AccessTools.Field(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.m_quality));
        private static readonly MethodInfo SafeQualityMethod = AccessTools.Method(typeof(RecipeGetAmountNrePatch), nameof(SafeQuality));

        // A missing item contributes nothing: quality 1 makes the (quality - 1) term zero.
        private static int SafeQuality(ItemDrop.ItemData item) => item?.m_quality ?? 1;

        // Priority.Last, for the reason in ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(nameof(Recipe.GetAmount))]
        private static IEnumerable<CodeInstruction> GetAmountTranspiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);
            if (Enabled == null || !Enabled.Value) { return codes; }

            int patched = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (!codes[i].LoadsField(QualityField)) { continue; }

                // The ItemData reference is already on the stack for the field load, so a static call
                // taking that same reference and returning int is a drop-in replacement.
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = SafeQualityMethod;
                patched++;
            }

            if (patched == 0) {
                Logger.LogWarning(
                    "Recipe.GetAmount: found no m_quality load to guard, so this fix is inactive. Another " +
                    "mod has most likely already rewritten the method - if so, nothing is wrong.");
                return instructions;
            }

            return codes;
        }
    }
}
