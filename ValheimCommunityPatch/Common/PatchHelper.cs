using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>Helpers shared by this mod's patches.</summary>
    internal static class PatchHelper {
        /// <summary>Pass as <c>expected</c> to <see cref="ReplaceCalls"/> to accept any non-zero count.</summary>
        internal const int AnyCount = -1;

        /// <summary>A deep copy of a transpiler's input, safe to rewrite in place.</summary>
        /// <remarks>
        /// Harmony hands the same CodeInstruction objects to every transpiler in the chain, so a
        /// plain <c>new List&lt;CodeInstruction&gt;(instructions)</c> would let an in-place edit
        /// leak into the input. Every transpiler here bails out by returning the untouched input
        /// when it finds something unexpected, and that bail-out is only honest over a real copy.
        /// </remarks>
        internal static List<CodeInstruction> Copy(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> copy = instructions is ICollection<CodeInstruction> known
                ? new List<CodeInstruction>(known.Count)
                : new List<CodeInstruction>();

            foreach (CodeInstruction instruction in instructions) { copy.Add(new CodeInstruction(instruction)); }

            return copy;
        }

        /// <summary>
        /// Rewrites every call to <paramref name="original"/> into a static call to
        /// <paramref name="replacement"/>, which must take the same stack and return the same type.
        /// </summary>
        /// <remarks>
        /// Returns the input untouched, and logs why, when the number of calls found is not
        /// <paramref name="expected"/> (or is zero for <see cref="AnyCount"/>). That usually means
        /// another mod has already rewritten the method, in which case standing down is the right
        /// outcome; see the Priority.Last note in ValheimCommunityPatch.ApplyPatches.
        /// </remarks>
        internal static IEnumerable<CodeInstruction> ReplaceCalls(
            IEnumerable<CodeInstruction> instructions, MethodInfo original, MethodInfo replacement,
            string site, int expected = AnyCount) {
            if (original == null || replacement == null) {
                Logger.LogWarning($"{site}: a method this fix needs could not be resolved, so it is inactive here.");
                return instructions;
            }

            List<CodeInstruction> codes = Copy(instructions);

            int found = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (!codes[i].Calls(original)) { continue; }

                codes[i].opcode = OpCodes.Call;
                codes[i].operand = replacement;
                found++;
            }

            bool ok = expected == AnyCount ? found > 0 : found == expected;
            if (ok) { return codes; }

            string wanted = expected == AnyCount ? "at least 1" : expected.ToString();
            Logger.LogWarning(
                $"{site}: expected {wanted} call(s) to {original.DeclaringType?.Name}.{original.Name}, " +
                $"found {found}, so this fix is inactive here. Another mod has most likely already " +
                "rewritten the method - if so, nothing is wrong.");
            return instructions;
        }

        /// <summary>
        /// True when <paramref name="target"/> carries a prefix or postfix declared by
        /// <paramref name="hookClass"/> and owned by this mod.
        /// </summary>
        internal static bool HasHook(MethodBase target, Type hookClass) {
            // Fully qualified: HarmonyLib.Patches collides with this mod's Patches namespace.
            HarmonyLib.Patches info = target == null ? null : Harmony.GetPatchInfo(target);
            if (info == null) { return false; }

            return DeclaredBy(info.Prefixes, hookClass) || DeclaredBy(info.Postfixes, hookClass);
        }

        private static bool DeclaredBy(IReadOnlyList<Patch> patches, Type hookClass) {
            foreach (Patch patch in patches) {
                if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                if (patch.PatchMethod?.DeclaringType == hookClass) { return true; }
            }

            return false;
        }
    }

    /// <summary>
    /// A once-only check that every hook a fix depends on actually attached. A fix whose index or
    /// registry is fed by hooks must not trust it if any hook is missing, so each read path asks
    /// <see cref="Healthy"/> and stands down to vanilla when it is false.
    /// </summary>
    /// <remarks>
    /// Evaluated lazily because Harmony cannot be asked about patches until they are all applied,
    /// and only once because the answer cannot change within a session.
    /// </remarks>
    internal sealed class HookHealth {
        private readonly string _fixName;
        private readonly Func<bool> _allAttached;
        private bool _checked;
        private bool _healthy;

        internal HookHealth(string fixName, Func<bool> allAttached) {
            _fixName = fixName;
            _allAttached = allAttached;
        }

        internal bool Healthy {
            get {
                if (_checked) { return _healthy; }

                _checked = true;
                _healthy = _allAttached();
                if (!_healthy) {
                    Logger.LogError(
                        $"{_fixName}: a maintenance hook is not attached, so this fix stands down to " +
                        "vanilla for this session. A Valheim update has most likely changed one of the " +
                        "patched methods - look for the patch failure logged at startup.");
                }

                return _healthy;
            }
        }
    }
}
