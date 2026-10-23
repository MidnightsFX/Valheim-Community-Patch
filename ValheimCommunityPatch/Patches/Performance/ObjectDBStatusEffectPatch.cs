using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Status Effect Lookup Scan: ObjectDB.GetStatusEffect answers from a map keyed by name hash
    // instead of walking every status effect in the game.
    //
    // GetStatusEffect compares the hash with NameHash() of each entry in m_StatusEffects, front to
    // back. The game asks it each time a character gains an effect it does not already have, and mods
    // ask it for their own effects; large mod packs register hundreds.
    //
    // A prefix returns the same effect, the first in list order with that hash, from a map built on
    // first use. The map is rebuilt whenever m_StatusEffects is replaced or its contents change, read
    // from the list's own mutation counter, so effects mods add, remove or swap while playing are
    // found. A list holding an entry vanilla's loop would throw on is left to vanilla. It runs after
    // other mods' prefixes and respects one that already replaced the call, and stands down for the
    // session when another mod transpiles the method.
    //
    // Client: the callers are characters' owners. Provenance: contributed by searica (PR #22).
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.GetStatusEffect))]
    internal static class ObjectDBStatusEffectPatch {
        private const string FixName = "Fix Status Effect Lookup Scan";

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(ObjectDBStatusEffectPatch));

        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            typeof(ObjectDBStatusEffectPatch),
            AccessTools.DeclaredMethod(typeof(ObjectDB), nameof(ObjectDB.GetStatusEffect)),
            HookKinds.Transpilers,
            owners => $"Status effect lookup is changed by {owners}, so '{FixName}' stands down and " +
                      "that mod's version applies.");

        private static readonly AccessTools.FieldRef<List<StatusEffect>, int> ListVersion = ResolveListVersion();

        private static readonly Dictionary<int, StatusEffect> ByHash = new Dictionary<int, StatusEffect>();
        private static List<StatusEffect> _cachedList;
        private static int _cachedVersion;
        private static bool _cacheUsable;

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(ObjectDB __instance, int nameHash, ref StatusEffect __result, bool __runOriginal) {
            if (!__runOriginal) { return false; }
            if (ApiSwitch.Off || ListVersion == null) { return true; }

            // Vanilla throws on a missing list; it still does.
            List<StatusEffect> effects = __instance.m_StatusEffects;
            if (effects == null) { return true; }
            if (Takeover.TakenOver) { return true; }

            int version = ListVersion(effects);
            if (!ReferenceEquals(effects, _cachedList) || version != _cachedVersion) {
                _cachedList = effects;
                _cachedVersion = version;
                _cacheUsable = Rebuild(effects);
            }

            if (!_cacheUsable) { return true; }

            // Null when no effect has that hash, as vanilla.
            ByHash.TryGetValue(nameHash, out __result);
            return false;
        }

        // False when an entry would make vanilla's loop throw, so the lookup is left to it.
        private static bool Rebuild(List<StatusEffect> effects) {
            ByHash.Clear();
            for (int i = 0; i < effects.Count; i++) {
                StatusEffect effect = effects[i];
                if ((object)effect == null) { return false; }

                int hash;
                try {
                    hash = effect.NameHash();
                } catch (Exception e) {
                    // A destroyed asset whose hash was never read: its name can no longer be.
                    Logger.LogDebug($"{FixName}: status effect {i} cannot be read ({e.GetType().Name}).");
                    return false;
                }

                // The first in list order wins, as in vanilla's loop.
                if (!ByHash.ContainsKey(hash)) { ByHash.Add(hash, effect); }
            }

            return true;
        }

        // Probed on a real list rather than trusted: a counter that never moves would keep serving
        // a list that mods had edited.
        private static AccessTools.FieldRef<List<StatusEffect>, int> ResolveListVersion() {
            try {
                AccessTools.FieldRef<List<StatusEffect>, int> version = AccessTools.FieldRefAccess<List<StatusEffect>, int>("_version");
                List<StatusEffect> probe = new List<StatusEffect>();
                int before = version(probe);
                probe.Add(null);
                if (version(probe) != before) { return version; }
            } catch (Exception e) {
                Logger.LogDebug($"List mutation counter unavailable ({e.GetType().Name}).");
            }

            Logger.LogWarning(
                $"{FixName}: the status effect list's change counter cannot be read, so this fix is " +
                "inactive and the game's own lookup is used.");
            return null;
        }
    }
}
