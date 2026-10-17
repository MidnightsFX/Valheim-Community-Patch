using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Recipe Lookup Scan: ObjectDB.GetRecipe answers from a cache keyed by item name instead of
    // walking the whole recipe list on every call.
    //
    // GetRecipe compares the item's name with the item of each recipe in m_recipes, front to back.
    // The crafting screen calls it for every worn item on every frame it is open at a station, to
    // decide whether the repair button glows, and item tooltips call it as well. With the recipe
    // lists large mod packs build, that is thousands of string comparisons a frame.
    //
    // A prefix returns the same recipe, the first whose item has that name, from a dictionary
    // filled on first use of each name; names with no recipe are cached too. The cache is dropped
    // whenever m_recipes is replaced or its contents change, read from the list's own mutation
    // counter, so recipes mods add or remove while playing are found, and a cached recipe whose item
    // has since changed is looked up again. Where vanilla's loop would throw, vanilla runs and does.
    // It runs after other mods' prefixes and respects one that already replaced the call, and
    // stands down for the session when another mod transpiles the method.
    //
    // Client: every caller in the game is UI. Provenance: ComfyMods/ComfyAutoRepair (GPL-3.0,
    // redseiko), whose RecipeManager cache this is, with change detection added.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.GetRecipe))]
    internal static class RecipeLookupPatch {
        private const string FixName = "Fix Recipe Lookup Scan";

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(RecipeLookupPatch));

        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            typeof(RecipeLookupPatch),
            AccessTools.DeclaredMethod(typeof(ObjectDB), nameof(ObjectDB.GetRecipe)),
            HookKinds.Transpilers,
            owners => $"Recipe lookup is changed by {owners}, so '{FixName}' stands down and " +
                      "that mod's version applies.");

        private static readonly AccessTools.FieldRef<List<Recipe>, int> ListVersion = ResolveListVersion();

        // A null value is a name no recipe makes.
        private static readonly Dictionary<string, Recipe> ByName = new Dictionary<string, Recipe>(StringComparer.Ordinal);
        private static List<Recipe> _cachedList;
        private static int _cachedVersion;

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(ObjectDB __instance, ItemDrop.ItemData item, ref Recipe __result, bool __runOriginal) {
            if (!__runOriginal) { return false; }
            if (ApiSwitch.Off || ListVersion == null) { return true; }

            // Vanilla throws on a missing item or list and can match a null name; it still does.
            List<Recipe> recipes = __instance.m_recipes;
            string name = item?.m_shared?.m_name;
            if (recipes == null || name == null) { return true; }
            if (Takeover.TakenOver) { return true; }

            int version = ListVersion(recipes);
            if (!ReferenceEquals(recipes, _cachedList) || version != _cachedVersion) {
                ByName.Clear();
                _cachedList = recipes;
                _cachedVersion = version;
            }

            if (ByName.TryGetValue(name, out Recipe cached)
                && ((object)cached == null || (TryMatch(cached, name, out bool stillMatches) && stillMatches))) {
                __result = cached;
                return false;
            }

            Recipe found = null;
            for (int i = 0; i < recipes.Count; i++) {
                Recipe recipe = recipes[i];
                if (!TryMatch(recipe, name, out bool match)) { return true; }
                if (match) {
                    found = recipe;
                    break;
                }
            }

            ByName[name] = found;
            __result = found;
            return false;
        }

        // False where vanilla's comparison would throw on this recipe, so the caller can let it.
        private static bool TryMatch(Recipe recipe, string name, out bool match) {
            match = false;
            if ((object)recipe == null) { return false; }
            if (recipe.m_item == null) { return true; }

            ItemDrop.ItemData.SharedData shared = recipe.m_item.m_itemData?.m_shared;
            if (shared == null) { return false; }

            match = shared.m_name == name;
            return true;
        }

        // Probed on a real list rather than trusted: a counter that never moves would keep serving
        // a list that mods had edited.
        private static AccessTools.FieldRef<List<Recipe>, int> ResolveListVersion() {
            try {
                AccessTools.FieldRef<List<Recipe>, int> version = AccessTools.FieldRefAccess<List<Recipe>, int>("_version");
                List<Recipe> probe = new List<Recipe>();
                int before = version(probe);
                probe.Add(null);
                if (version(probe) != before) { return version; }
            } catch (Exception e) {
                Logger.LogDebug($"List mutation counter unavailable ({e.GetType().Name}).");
            }

            Logger.LogWarning(
                $"{FixName}: the recipe list's change counter cannot be read, so this fix is inactive " +
                "and the game's own lookup is used.");
            return null;
        }
    }
}
