using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Hotbar Key Allocation: Player.Update formats up to sixteen hotbar key names over boxed ints every frame.
    // Transpiles Player.Update to swap each "Hotbar{0}" string.Format call for a lookup of names cached at startup.
    // Taking input no longer makes garbage strings and boxes every frame.
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

                // Stack: [] -> [slot] -> [name]. Labels and blocks stay where they were: the ldstr becomes a
                // nop rather than being removed because it carries the loop head's label.
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
                PatchHelper.WarnOnce(
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
