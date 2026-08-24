using System.Collections.Generic;
using HarmonyLib;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>Helpers shared by this mod's transpilers.</summary>
    internal static class PatchHelper {
        /// <summary>
        /// A genuinely private copy of a transpiler's input, safe to rewrite in place.
        /// </summary>
        /// <remarks>
        /// <c>new List&lt;CodeInstruction&gt;(instructions)</c> looks like it does this, and does not.
        /// It copies the list, not the instructions: every element is still the object Harmony handed
        /// in, and Harmony hands the same objects to every transpiler in the chain. Rewriting an
        /// opcode or operand through that list therefore edits the caller's stream as well.
        ///
        /// Which matters because every transpiler here counts what it rewrote and returns the input
        /// untouched when the count is wrong. That tolerance is deliberate - it is what lets this mod
        /// share a method with a mod using CodeMatcher.ThrowIfNotMatch, as described in
        /// ValheimCommunityPatch.ApplyPatches. Over a shallow copy the bail-out is a lie: the rewrites
        /// are already in the stream being handed back, so a fix that decided it was unsafe to apply
        /// gets applied anyway, and half-finished at that. Three of the nine bail-outs in this mod
        /// could reach a non-zero count and were doing exactly that.
        ///
        /// The element copy is Harmony's own CodeInstruction.Clone semantics - opcode, operand, and
        /// fresh lists of labels and blocks. Labels are value types, so branch targets still resolve
        /// through the copy.
        /// </remarks>
        internal static List<CodeInstruction> Copy(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> copy = instructions is ICollection<CodeInstruction> known
                ? new List<CodeInstruction>(known.Count)
                : new List<CodeInstruction>();

            foreach (CodeInstruction instruction in instructions) { copy.Add(new CodeInstruction(instruction)); }

            return copy;
        }
    }
}
