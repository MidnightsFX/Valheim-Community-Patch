using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Recipe Lookup Scan: GetRecipe scans every recipe, for each worn item, every frame a crafting station is open.
    // Prefixes GetRecipe to answer from a cache keyed by item name, dropped whenever the recipe list changes.
    // The crafting screen and tooltips stay cheap with the large recipe lists mod packs build.
    // Provenance: ComfyMods/ComfyAutoRepair (GPL-3.0, redseiko), its RecipeManager cache with change detection added.
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

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches. Respects another mod's prefix that already
        // replaced the call.
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
