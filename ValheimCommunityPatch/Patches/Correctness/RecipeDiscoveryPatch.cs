using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Recipe Discovery: learns a recipe as soon as its crafting ingredients are known, even while
    // the player stands at a crafting station.
    //
    // Since the Deep North update more than half the recipes also list an upgrade item (Upgrader2Weapon
    // on the Bronze Axe, Upgrader4Weapon on the FrostFire weapons) that is only spent at an upgrade
    // station. Player.HaveRequirementItems skips those upgrade-only ingredients in discovery mode when
    // the player is at no station, but counts them when the player is at any station. Crafting Bronze
    // at the forge or a FrostFire orb at the black forge therefore never unlocked the recipes that use
    // it, and they appeared only after a later pickup away from the station.
    //
    // A postfix on HaveRequirementItems, for discovery calls the game answered false while the player
    // is at a station, checks the recipe again without its upgrade-only ingredients: the same answer
    // the game gives away from a station. It only ever turns a false into a true. It runs before other
    // postfixes so a mod that withholds recipes still has the last word, and it stands down when
    // another mod's prefix replaced the method.
    //
    // Client: recipes are discovered by the local player.
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
                "crafting station. Without it, recipes that also list an upgrade item, such as the Bronze " +
                "Axe or the FrostFire weapons, are only learned on a later pickup away from the station.");
        }

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
