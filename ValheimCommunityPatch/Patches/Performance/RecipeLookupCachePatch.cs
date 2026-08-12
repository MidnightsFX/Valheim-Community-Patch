using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: ObjectDB.GetRecipe is an unindexed linear scan over m_recipes doing a string
    // comparison on m_shared.m_name per recipe.
    //
    //   public Recipe GetRecipe(ItemDrop.ItemData item) {
    //     foreach (Recipe recipe in this.m_recipes)
    //       if (!(recipe.m_item == null) && recipe.m_item.m_itemData.m_shared.m_name == item.m_shared.m_name)
    //         return recipe;
    //     return null;
    //   }
    //
    // InventoryGui.Update calls UpdateRepair every frame the inventory is open, which calls
    // HaveRepairableItems -> CanRepair(item) -> GetRecipe for every worn item. That is
    // worn_items * total_recipes string comparisons *per frame*: a few thousand in vanilla, tens of
    // thousands with a modded ObjectDB. Pressing Repair then runs RepairOneItem + UpdateRepair +
    // UpdateCraftingPanel back to back, re-running the scan - the workbench repair hitch.
    //
    // GetRecipe is also on the upgrade panel and crafting-requirement paths, so indexing it is a
    // global win rather than a repair-specific one.
    //
    // Fix: memoize name -> Recipe. Vanilla semantics are preserved exactly, including first-recipe-wins
    // on duplicate names and a null result for items with no recipe.
    //
    // Provenance: same root cause and approach as ComfyMods/ComfyAutoRepair (GPL-3.0, redseiko),
    // reimplemented here with eager index construction and stricter cache invalidation.
    [HarmonyPatch(typeof(ObjectDB))]
    internal static class RecipeLookupCachePatch {
        internal static ConfigEntry<bool> Enabled;

        private static readonly Dictionary<string, Recipe> RecipesByItemName = new Dictionary<string, Recipe>();

        // The ObjectDB the index was built from, and the recipe count at that time. Either changing
        // means the index is stale. The count check is what catches mods (Jotunn's ItemManager among
        // them) that append to m_recipes after UpdateRegisters has already run.
        private static ObjectDB _indexedDb;
        private static int _indexedRecipeCount = -1;

        internal static void BindConfig() {
            Enabled = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Cache Recipe Lookups",
                true,
                "Indexes ObjectDB.GetRecipe by item name instead of scanning every recipe on every call. " +
                "Fixes the frame hitch and sustained FPS drop while the inventory is open at a crafting " +
                "station, which gets dramatically worse the more recipes other mods add.");
        }

        internal static void Invalidate() {
            RecipesByItemName.Clear();
            _indexedDb = null;
            _indexedRecipeCount = -1;
        }

        private static void Rebuild(ObjectDB db) {
            RecipesByItemName.Clear();

            List<Recipe> recipes = db.m_recipes;
            for (int i = 0; i < recipes.Count; i++) {
                Recipe recipe = recipes[i];

                // Vanilla would throw here on a null entry; skipping is strictly safer and matches
                // the "first match wins" result for every list that vanilla could handle at all.
                if (recipe == null || recipe.m_item == null) { continue; }

                ItemDrop.ItemData.SharedData shared = recipe.m_item.m_itemData?.m_shared;
                if (shared == null || shared.m_name == null) { continue; }

                // First recipe wins, exactly as the vanilla scan does.
                if (!RecipesByItemName.ContainsKey(shared.m_name)) {
                    RecipesByItemName.Add(shared.m_name, recipe);
                }
            }

            _indexedDb = db;
            _indexedRecipeCount = recipes.Count;
            Logger.LogDebug($"Indexed {RecipesByItemName.Count} recipes from {recipes.Count} entries.");
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ObjectDB.GetRecipe))]
        private static bool GetRecipePrefix(ObjectDB __instance, ItemDrop.ItemData item, ref Recipe __result) {
            if (Enabled == null || !Enabled.Value) { return true; }

            string itemName = item?.m_shared?.m_name;
            if (itemName == null) { return true; }

            if (__instance.m_recipes == null) { return true; }

            if (_indexedDb != __instance || _indexedRecipeCount != __instance.m_recipes.Count) {
                Rebuild(__instance);
            }

            RecipesByItemName.TryGetValue(itemName, out __result);
            return false;
        }

        // UpdateRegisters runs from both ObjectDB.Awake and CopyOtherDB, so this covers every point at
        // which the game itself swaps the recipe list out from under us.
        [HarmonyPostfix]
        [HarmonyPatch("UpdateRegisters")]
        private static void UpdateRegistersPostfix() {
            Invalidate();
        }
    }
}
