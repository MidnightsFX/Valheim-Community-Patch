using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Vanilla defect: ZDOMan.Load indexes every loaded ZDO with Dictionary.Add:
    //
    //   foreach (ZDO zdo in zdos) {
    //     this.m_objectsByID.Add(zdo.m_uid, zdo);
    //     ...
    //   }
    //
    // Add throws on a duplicate key. A save file containing two ZDOs with the same ZDOID - which
    // happens after a crash mid-save, a botched world merge, or a mod that mints its own IDs - takes
    // the whole world load down with an ArgumentException, and the world becomes unloadable.
    //
    // Fix: keep the last occurrence and log, rather than aborting. A duplicated ZDOID means one of the
    // two is already unreachable in vanilla's own index; recovering the world is strictly better than
    // refusing to open it.
    //
    // Server: ZDOMan.Load only runs on the host, via ZNet.Start's if (m_isServer) branch. A runtime
    // gate would be impossible here anyway - this is a transpiler.
    [PatchSide(Side.Server)]
    [HarmonyPatch(typeof(ZDOMan))]
    internal static class ZdoLoadDuplicatePatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ZdoLoadDuplicatePatch),
                ValConfig.SectionCorrectness,
                "Tolerate Duplicate ZDOs On Load",
                true,
                "Recovers a world whose save contains duplicate ZDO ids instead of aborting the load with " +
                "an exception. Changing this requires a game restart.");
        }

        private static readonly MethodInfo DictionaryAddMethod =
            AccessTools.Method(typeof(Dictionary<ZDOID, ZDO>), nameof(Dictionary<ZDOID, ZDO>.Add));
        private static readonly MethodInfo AddOrReplaceMethod =
            AccessTools.Method(typeof(ZdoLoadDuplicatePatch), nameof(AddOrReplace));

        private static void AddOrReplace(Dictionary<ZDOID, ZDO> objectsById, ZDOID uid, ZDO zdo) {
            if (objectsById.ContainsKey(uid)) {
                Logger.LogWarning(
                    $"Duplicate ZDO id {uid} in the save file; keeping the later one. " +
                    "This world was saved in a damaged state, but loading will continue.");
            }

            objectsById[uid] = zdo;
        }

        // Priority.Last, for the reason in ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(nameof(ZDOMan.Load))]
        private static IEnumerable<CodeInstruction> LoadTranspiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);
            if (Enabled == null || !Enabled.Value) { return codes; }

            int patched = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (!codes[i].Calls(DictionaryAddMethod)) { continue; }

                // Signature matches the instance call being replaced: (dictionary, key, value) are
                // already on the stack in that order.
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = AddOrReplaceMethod;
                patched++;
            }

            if (patched != 1) {
                Logger.LogWarning(
                    $"ZDOMan.Load: expected 1 ZDO dictionary insert, found {patched}, so this fix is " +
                    "inactive. Another mod has most likely already rewritten the method - if so, nothing " +
                    "is wrong.");
                return instructions;
            }

            return codes;
        }
    }
}
