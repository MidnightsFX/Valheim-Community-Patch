using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Hotbar Key Allocation: the hotbar keys are checked with cached key names instead of
    // sixteen freshly formatted strings every frame.
    //
    // Player.Update checks hotbar slots 1-8 every frame the local player takes input, asking
    // ZInput for "Hotbar{0}" and, unless that one is down, "Hotbar{0}Alt", each built with
    // string.Format over a boxed int: up to sixteen strings and sixteen boxes per frame, all garbage.
    //
    // A transpiler replaces each `ldstr; ldloc; box int; call string.Format` with the slot number
    // and a call that returns a cached name, built once with the same Format call so the strings
    // are identical. A slot outside the cache falls back to formatting. The ldstr becomes a nop
    // rather than being removed because it carries the loop head's label.
    //
    // Client: only the local player takes input.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(Player), "Update")]
    internal static class HotbarKeyAllocPatch {
        private const string KeyFormat = "Hotbar{0}";
        private const string AltKeyFormat = "Hotbar{0}Alt";

        // Vanilla asks for 1-8; mods that add slots usually stay well under this.
        private const int CachedSlots = 16;

        private static readonly string[] KeyNames = BuildNames(KeyFormat);
        private static readonly string[] AltKeyNames = BuildNames(AltKeyFormat);

        private static readonly MethodInfo FormatMethod =
            AccessTools.Method(typeof(string), nameof(string.Format), new[] { typeof(string), typeof(object) });

        private static readonly MethodInfo KeyNameMethod =
            AccessTools.Method(typeof(HotbarKeyAllocPatch), nameof(KeyName));

        private static readonly MethodInfo AltKeyNameMethod =
            AccessTools.Method(typeof(HotbarKeyAllocPatch), nameof(AltKeyName));

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            int replaced = 0;
            for (int i = 0; i + 3 < codes.Count; i++) {
                if (codes[i].opcode != OpCodes.Ldstr) { continue; }

                string format = codes[i].operand as string;
                MethodInfo lookup = format == KeyFormat ? KeyNameMethod : format == AltKeyFormat ? AltKeyNameMethod : null;
                if (lookup == null) { continue; }

                if (!codes[i + 1].IsLdloc()
                    || codes[i + 2].opcode != OpCodes.Box || !Equals(codes[i + 2].operand, typeof(int))
                    || !codes[i + 3].Calls(FormatMethod)) {
                    continue;
                }

                // Stack: [] -> [slot] -> [name]. Labels and blocks stay where they were.
                codes[i].opcode = OpCodes.Nop;
                codes[i].operand = null;
                codes[i + 2].opcode = OpCodes.Nop;
                codes[i + 2].operand = null;
                codes[i + 3].opcode = OpCodes.Call;
                codes[i + 3].operand = lookup;

                replaced++;
                i += 3;
            }

            if (replaced != 2) {
                Logger.LogWarning(
                    $"Player.Update: expected 2 hotbar key formats, found {replaced}, so this fix is " +
                    "inactive. Another mod has most likely already rewritten the method - if so, " +
                    "nothing is wrong.");
                return instructions;
            }

            return codes;
        }

        private static string[] BuildNames(string format) {
            string[] names = new string[CachedSlots];
            for (int i = 0; i < CachedSlots; i++) { names[i] = string.Format(format, i); }

            return names;
        }

        private static string KeyName(int slot) =>
            (uint)slot < CachedSlots ? KeyNames[slot] : string.Format(KeyFormat, slot);

        private static string AltKeyName(int slot) =>
            (uint)slot < CachedSlots ? AltKeyNames[slot] : string.Format(AltKeyFormat, slot);
    }
}
