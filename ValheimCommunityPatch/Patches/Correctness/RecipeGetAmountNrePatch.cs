using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Recipe.GetAmount reads m_quality from a null item when the player has none of an any-one-of recipe's items.
    // A transpiler replaces that m_quality load with a call that returns 1 for a null item, leaving the math unchanged.
    // The crafting and upgrade panels show such recipes instead of breaking.
    // Provenance: Zen.ModLib (catalogue).
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Recipe))]
    internal static class RecipeGetAmountNrePatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(RecipeGetAmountNrePatch),
                ValConfig.SectionCorrectness,
                "Fix Recipe Amount Crash",
                true,
                "Stops the crafting and upgrade panel breaking on a 'requires any one of these' recipe when you " +
                "carry none of its ingredients; turning it on needs a game restart.");
        }

        private static readonly FieldInfo QualityField = AccessTools.Field(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.m_quality));
        private static readonly MethodInfo SafeQualityMethod = AccessTools.Method(typeof(RecipeGetAmountNrePatch), nameof(SafeQuality));

        // Turned off, a null item throws as it does in vanilla.
        private static int SafeQuality(ItemDrop.ItemData item) => item == null && Enabled.Value ? 1 : item.m_quality;

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(nameof(Recipe.GetAmount))]
        private static IEnumerable<CodeInstruction> GetAmountTranspiler(IEnumerable<CodeInstruction> instructions) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            // The ItemData reference is already on the stack for the field load, so a static call
            // taking that reference and returning int is a drop-in replacement.
            int patched = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (!codes[i].LoadsField(QualityField)) { continue; }

                codes[i].opcode = OpCodes.Call;
                codes[i].operand = SafeQualityMethod;
                patched++;
            }

            if (patched == 0) {
                PatchHelper.WarnOnce(
                    "Recipe.GetAmount: found no m_quality load to guard, so this fix is inactive. Another " +
                    "mod has most likely already rewritten the method - if so, nothing is wrong.");
                return instructions;
            }

            return codes;
        }
    }
}
