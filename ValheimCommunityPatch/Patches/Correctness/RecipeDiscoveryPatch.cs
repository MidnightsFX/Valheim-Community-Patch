using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // At a station HaveRequirementItems counts Deep North's upgrade-only ingredients, so their recipes stay hidden.
    // A postfix rechecks a failed discovery at a station without upgrade-only ingredients, as vanilla does elsewhere.
    // Recipes unlock as soon as their crafting ingredients are known, wherever the player stands.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Player))]
    internal static class RecipeDiscoveryPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(RecipeDiscoveryPatch),
                ValConfig.SectionCorrectness,
                "Fix Recipe Discovery",
                true,
                "Learns a recipe as soon as you know its crafting ingredients, even while you stand at a " +
                "crafting station.");
        }

        // Only ever turns a false into a true. First among postfixes, so a mod that withholds recipes
        // still has the last word; stands down when another mod's prefix replaced the method.
        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(nameof(Player.HaveRequirementItems), typeof(Recipe), typeof(bool), typeof(int), typeof(int))]
        private static void HaveRequirementItemsPostfix(Player __instance, Recipe piece, bool discover, ref bool __result, bool __runOriginal) {
            if (__result || !discover || !__runOriginal || Enabled == null || !Enabled.Value) { return; }

            // Away from a station the game already left the upgrade-only ingredients out.
            if (piece == null || piece.m_resources == null || __instance.GetCurrentCraftingStation() == null) { return; }

            __result = KnowsCraftingIngredients(__instance, piece);
        }

        // The game's discovery test with upgrade-only ingredients left out.
        private static bool KnowsCraftingIngredients(Player player, Recipe recipe) {
            bool anyOne = recipe.m_requireOnlyOneIngredient;

            foreach (Piece.Requirement requirement in recipe.m_resources) {
                if (requirement == null || requirement.m_upgraderResource || !requirement.m_resItem || requirement.m_amount <= 0) { continue; }

                bool known = player.m_knownMaterial.Contains(requirement.m_resItem.m_itemData.m_shared.m_name);
                if (anyOne && known) { return true; }
                if (!anyOne && !known) { return false; }
            }

            return !anyOne;
        }
    }
}
