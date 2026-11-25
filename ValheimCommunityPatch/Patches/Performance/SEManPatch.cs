using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Active Status Effect Lookup: SEMan.GetStatusEffect walks the effect list, and every AddStatusEffect calls it.
    // Hooks on add, remove, expiry and teardown keep a name-hash map, and a prefix answers GetStatusEffect from it.
    // The player's per-frame environment effects cost one map read each instead of a list walk.
    // Provenance: contributed by searica (PR #22).
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class SEManPatch {
        private const string FixName = "Fix Active Status Effect Lookup";

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(SEManPatch));

        private static readonly MethodBase ConstructorMethod =
            AccessTools.DeclaredConstructor(typeof(SEMan), new[] { typeof(Character), typeof(ZNetView) });
        private static readonly MethodInfo OnDestroyMethod = AccessTools.DeclaredMethod(typeof(SEMan), nameof(SEMan.OnDestroy));
        private static readonly MethodInfo AddMethod = AccessTools.DeclaredMethod(typeof(SEMan), nameof(SEMan.AddStatusEffect),
            new[] { typeof(StatusEffect), typeof(bool), typeof(int), typeof(float), typeof(short) });
        private static readonly MethodInfo RemoveMethod = AccessTools.DeclaredMethod(typeof(SEMan), nameof(SEMan.RemoveStatusEffect),
            new[] { typeof(int), typeof(bool) });
        private static readonly MethodInfo RemoveAllMethod = AccessTools.DeclaredMethod(typeof(SEMan), nameof(SEMan.RemoveAllStatusEffects));
        private static readonly FieldInfo RemoveListField = AccessTools.Field(typeof(SEMan), nameof(SEMan.m_removeStatusEffects));
        private static readonly MethodInfo ListClearMethod = AccessTools.Method(typeof(List<StatusEffect>), nameof(List<StatusEffect>.Clear));
        private static readonly MethodInfo ForgetExpiredMethod = AccessTools.Method(typeof(SEManPatch), nameof(ForgetExpired));

        // Set by the Update transpiler, which Harmony re-runs each time any mod patches Update.
        private static bool _expiryHooked;

        // A missing hook leaves maps holding effects that are gone, and a missing OnDestroy hook
        // would keep every unloaded character alive, so the answer gates the fix entirely.
        private static readonly HookHealth Hooks = new HookHealth(
            typeof(SEManPatch),
            FixName,
            () => _expiryHooked
               && PatchHelper.HasHook(ConstructorMethod, typeof(SEManPatch))
               && PatchHelper.HasHook(OnDestroyMethod, typeof(SEManPatch))
               && PatchHelper.HasHook(AddMethod, typeof(SEManPatch))
               && PatchHelper.HasHook(RemoveMethod, typeof(SEManPatch))
               && PatchHelper.HasHook(RemoveAllMethod, typeof(SEManPatch)));

        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            typeof(SEManPatch),
            AccessTools.DeclaredMethod(typeof(SEMan), nameof(SEMan.GetStatusEffect), new[] { typeof(int) }),
            HookKinds.Transpilers,
            owners => $"Active status effect lookup is changed by {owners}, so '{FixName}' stands down " +
                      "and that mod's version applies.");

        // Name hash to active effect, per SEMan.
        private static readonly Dictionary<SEMan, Dictionary<int, StatusEffect>> Tracked =
            new Dictionary<SEMan, Dictionary<int, StatusEffect>>();

        // The SEMan last used and its map, null when it is not tracked. In play, finding the map in
        // Tracked was most of what a lookup cost, more than vanilla's walk of a short effect list.
        // Cleared when that SEMan is destroyed, so it never keeps one alive.
        private static SEMan _lastSeMan;
        private static Dictionary<int, StatusEffect> _lastActive;

        private static Dictionary<int, StatusEffect> ActiveOf(SEMan seMan) {
            if (!ReferenceEquals(seMan, _lastSeMan)) {
                Tracked.TryGetValue(seMan, out _lastActive);
                _lastSeMan = seMan;
            }

            return _lastActive;
        }

        // ---- lookup ------------------------------------------------------------------------

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.GetStatusEffect), typeof(int))]
        private static bool GetStatusEffectPrefix(SEMan __instance, int nameHash, ref StatusEffect __result, bool __runOriginal) {
            if (!__runOriginal) { return false; }
            if (ApiSwitch.Off || !Hooks.Healthy || Takeover.TakenOver) { return true; }

            // Vanilla's own early outs, so the map never returns an effect the set no longer holds.
            if (nameHash == 0 || !__instance.m_statusEffectsHashSet.Contains(nameHash)) {
                __result = null;
                return false;
            }

            Dictionary<int, StatusEffect> active = ActiveOf(__instance);
            if (active != null && active.TryGetValue(nameHash, out StatusEffect effect)) {
                __result = effect;
                return false;
            }

            return true;
        }

        // ---- map maintenance ---------------------------------------------------------------
        // These run even while the fix is switched off, so the maps are right when it is back on.
        // Removal itself stays vanilla's, which has to search the list to remove from it anyway.

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SEMan), MethodType.Constructor, typeof(Character), typeof(ZNetView))]
        private static void ConstructorPostfix(SEMan __instance) {
            // Nothing is tracked while a hook is missing, so a missing OnDestroy hook cannot leak.
            if (!Hooks.Healthy) { return; }

            Dictionary<int, StatusEffect> active = new Dictionary<int, StatusEffect>();
            Tracked[__instance] = active;

            // Another mod's constructor postfix may already have used it, leaving it cached untracked.
            if (ReferenceEquals(__instance, _lastSeMan)) { _lastActive = active; }
        }

        // A prefix, so the map goes even when an effect's own OnDestroy throws.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.OnDestroy))]
        private static void OnDestroyPrefix(SEMan __instance) {
            Tracked.Remove(__instance);

            if (ReferenceEquals(__instance, _lastSeMan)) {
                _lastSeMan = null;
                _lastActive = null;
            }
        }

        // Every add ends here: the int overload resolves the effect and calls this one.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.AddStatusEffect),
            typeof(StatusEffect), typeof(bool), typeof(int), typeof(float), typeof(short))]
        private static void AddStatusEffectPostfix(SEMan __instance, StatusEffect __result) {
            if ((object)__result == null) { return; }

            Dictionary<int, StatusEffect> active = ActiveOf(__instance);
            if (active != null) { active[__result.NameHash()] = __result; }
        }

        // Vanilla removes the effect and reports whether it did; the map only follows. The player's
        // environment update removes effects the player does not have on every tick, and when nothing
        // was removed there is nothing to forget, as the map only holds effects the list held. A mod
        // prefix that skips the removal to keep an effect reports false too, so its entry stays.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.RemoveStatusEffect), typeof(int), typeof(bool))]
        private static void RemoveStatusEffectPostfix(SEMan __instance, int nameHash, bool __result) {
            if (!__result) { return; }

            ActiveOf(__instance)?.Remove(nameHash);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.RemoveAllStatusEffects))]
        private static void RemoveAllStatusEffectsPostfix(SEMan __instance) => ActiveOf(__instance)?.Clear();

        // Expired effects leave through Update's own removal loop, which calls nothing hookable, so
        // this inserts a call just before that loop's list is cleared:
        //   ldarg.0; ldfld m_removeStatusEffects; callvirt List<StatusEffect>.Clear
        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.Update))]
        private static IEnumerable<CodeInstruction> UpdateTranspiler(IEnumerable<CodeInstruction> instructions) {
            _expiryHooked = false;

            // Checked here because a pattern built from a null member would match any operand.
            if (RemoveListField == null || ListClearMethod == null || ForgetExpiredMethod == null) {
                PatchHelper.WarnOnce($"{FixName}: a member this fix needs could not be resolved, so it is inactive.");
                return instructions;
            }

            // CodeMatcher works on copies, so the input stays untouched for a bail-out.
            CodeMatcher matcher = new CodeMatcher(instructions).End().MatchStartBackwards(
                new CodeMatch(OpCodes.Ldarg_0),
                new CodeMatch(OpCodes.Ldfld, RemoveListField),
                new CodeMatch(OpCodes.Callvirt, ListClearMethod));
            if (matcher.IsInvalid) {
                PatchHelper.WarnOnce(
                    $"{FixName}: SEMan.Update no longer clears its expired effects where expected, so " +
                    "this fix is inactive. Another mod has most likely already rewritten the method - if " +
                    "so, nothing is wrong.");
                return instructions;
            }

            // The anchor is where the removal loop's leave lands, so its labels move to the inserted
            // code; left on the anchor, the jump would skip the call.
            CodeInstruction load = new CodeInstruction(OpCodes.Ldarg_0)
                .MoveLabelsFrom(matcher.Instruction)
                .MoveBlocksFrom(matcher.Instruction);
            matcher.Insert(load, new CodeInstruction(OpCodes.Call, ForgetExpiredMethod));

            _expiryHooked = true;
            return matcher.InstructionEnumeration();
        }

        private static void ForgetExpired(SEMan seMan) {
            Dictionary<int, StatusEffect> active = ActiveOf(seMan);
            if (active == null) { return; }

            foreach (StatusEffect effect in seMan.m_removeStatusEffects) {
                int hash = effect.NameHash();

                // Only the instance that expired, never a newer one under the same name.
                if (active.TryGetValue(hash, out StatusEffect tracked) && ReferenceEquals(tracked, effect)) {
                    active.Remove(hash);
                }
            }
        }
    }
}
