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
        /// True when <paramref name="target"/> carries a prefix, postfix or finalizer declared by
        /// <paramref name="hookClass"/> and owned by this mod.
        /// </summary>
        internal static bool HasHook(MethodBase target, Type hookClass) {
            // Fully qualified: HarmonyLib.Patches collides with this mod's Patches namespace.
            HarmonyLib.Patches info = target == null ? null : Harmony.GetPatchInfo(target);
            if (info == null) { return false; }

            return DeclaredBy(info.Prefixes, hookClass) || DeclaredBy(info.Postfixes, hookClass)
                || DeclaredBy(info.Finalizers, hookClass);
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

    /// <summary>Which of other mods' hooks on a method a <see cref="TakeoverCheck"/> counts.</summary>
    [Flags]
    internal enum HookKinds {
        /// <summary>Prefixes that return bool, so can skip the method.</summary>
        BoolPrefixes = 1,
        /// <summary>Every prefix, void ones included.</summary>
        Prefixes = 2,
        Postfixes = 4,
        Transpilers = 8,
        Finalizers = 16,
        Any = Prefixes | Postfixes | Transpilers | Finalizers,
    }

    /// <summary>
    /// A once-only check that stands a fix down when another mod can take over a method the fix
    /// builds on: by default a prefix that returns bool, which can skip the method, and optionally
    /// a transpiler, which can rewrite it.
    /// </summary>
    /// <remarks>
    /// A bool prefix counts whether or not it skips any particular call, since one that ever does
    /// does that work somewhere the fix's hooks cannot follow. A fix that moves a method's work
    /// somewhere else, where even other mods' postfixes would no longer see it happen, counts every
    /// kind of hook. Evaluated lazily, at the fix's first use in a world, when every mod has
    /// patched; the answer cannot change within a session.
    /// </remarks>
    internal sealed class TakeoverCheck {
        private readonly MethodBase _target;
        private readonly HookKinds _kinds;
        private readonly Func<string, string> _message;
        private bool _checked;
        private bool _takenOver;

        /// <param name="message">The log line, given the other mods' GUIDs.</param>
        internal TakeoverCheck(MethodBase target, bool transpilers, Func<string, string> message)
            : this(target, HookKinds.BoolPrefixes | (transpilers ? HookKinds.Transpilers : 0), message) { }

        /// <param name="message">The log line, given the other mods' GUIDs.</param>
        internal TakeoverCheck(MethodBase target, HookKinds kinds, Func<string, string> message) {
            _target = target;
            _kinds = kinds;
            _message = message;
        }

        internal bool TakenOver {
            get {
                if (_checked) { return _takenOver; }

                _checked = true;

                SortedSet<string> owners = new SortedSet<string>(StringComparer.Ordinal);

                // Fully qualified: HarmonyLib.Patches collides with this mod's Patches namespace.
                HarmonyLib.Patches info = _target == null ? null : Harmony.GetPatchInfo(_target);
                if (info != null) {
                    bool anyPrefix = (_kinds & HookKinds.Prefixes) != 0;
                    bool boolPrefix = (_kinds & HookKinds.BoolPrefixes) != 0;
                    foreach (Patch patch in info.Prefixes) {
                        if (anyPrefix || (boolPrefix && patch.PatchMethod?.ReturnType == typeof(bool))) {
                            AddForeign(owners, patch);
                        }
                    }

                    if ((_kinds & HookKinds.Postfixes) != 0) { AddForeign(owners, info.Postfixes); }
                    if ((_kinds & HookKinds.Transpilers) != 0) { AddForeign(owners, info.Transpilers); }
                    if ((_kinds & HookKinds.Finalizers) != 0) { AddForeign(owners, info.Finalizers); }
                }

                _takenOver = owners.Count > 0;
                if (_takenOver) { Logger.LogInfo(_message(string.Join(", ", owners))); }

                return _takenOver;
            }
        }

        private static void AddForeign(SortedSet<string> owners, IReadOnlyList<Patch> patches) {
            foreach (Patch patch in patches) { AddForeign(owners, patch); }
        }

        private static void AddForeign(SortedSet<string> owners, Patch patch) {
            if (patch.owner != ValheimCommunityPatch.PluginGUID) { owners.Add(patch.owner); }
        }
    }
}
