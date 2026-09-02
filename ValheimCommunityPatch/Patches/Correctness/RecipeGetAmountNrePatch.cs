using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Recipe Amount Crash: stops the crafting panel throwing on a "requires any one of these"
    // recipe when the player carries none of the ingredients.
    //
    // Recipe.GetAmount reads singleReqItem.m_quality without a null check, and GetFirstRequiredItem
    // returns null when the player holds none of the accepted items. The NullReferenceException
    // leaves the crafting or upgrade panel blank.
    //
    // A transpiler replaces the m_quality field load with a call that returns 1 for a null item,
    // the quality a nonexistent item would contribute, so the rest of the calculation is unchanged.
    //
    // Client: GetAmount dereferences Player.m_localPlayer. Provenance: Zen.ModLib (catalogue).
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

        private static int SafeQuality(ItemDrop.ItemData item) => item?.m_quality ?? 1;

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
                Logger.LogWarning(
                    "Recipe.GetAmount: found no m_quality load to guard, so this fix is inactive. Another " +
                    "mod has most likely already rewritten the method - if so, nothing is wrong.");
                return instructions;
            }

            return codes;
        }
    }
}
