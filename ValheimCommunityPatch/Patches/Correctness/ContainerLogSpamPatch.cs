using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Container Log Spam: stops every chest open, stack-all and take-all writing several lines
    // to the game log.
    //
    // Container.RPC_RequestOpen, RPC_RequestStack and RPC_RequestTakeAll each log unconditionally
    // on entry and again on every rejection path. The handlers run on the container's owner, so a
    // player sorting their own base logs their own chests and a server logs the ones it owns.
    //
    // Transpilers point every ZLog.Log call in the three handlers at this mod's debug sink, so the
    // messages are silenced by default and come back with EnableDebugMode. The string building
    // that feeds the call stays; the log write and its stack trace are the bulk of the cost.
    //
    // Both. Provenance: same defect as ComfyMods/BetterZeeLog (GPL-3.0, redseiko), which removes
    // the calls; where both are installed its rewrite lands first and this one repoints whatever
    // it left.
    [PatchSide(Side.Both)]
    [HarmonyPatch]
    internal static class ContainerLogSpamPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ContainerLogSpamPatch),
                ValConfig.SectionCorrectness,
                "Fix Container Log Spam",
                true,
                "Stops every chest open, stack-all and take-all writing four lines to the game log. " +
                "The messages are still visible with EnableDebugMode on. Changing this requires a " +
                "game restart.");
        }

        private static readonly MethodInfo ZLogMethod = AccessTools.Method(typeof(ZLog), nameof(ZLog.Log));
        private static readonly MethodInfo SinkMethod = AccessTools.Method(typeof(Logger), nameof(Logger.DebugSink));

        private static IEnumerable<CodeInstruction> RedirectLogCalls(IEnumerable<CodeInstruction> instructions, string method) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            return PatchHelper.ReplaceCalls(instructions, ZLogMethod, SinkMethod, "Container." + method);
        }

        // Priority.Last on all three: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(Container), "RPC_RequestOpen")]
        private static IEnumerable<CodeInstruction> RequestOpenTranspiler(IEnumerable<CodeInstruction> instructions) =>
            RedirectLogCalls(instructions, "RPC_RequestOpen");

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(Container), "RPC_RequestStack")]
        private static IEnumerable<CodeInstruction> RequestStackTranspiler(IEnumerable<CodeInstruction> instructions) =>
            RedirectLogCalls(instructions, "RPC_RequestStack");

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(Container), "RPC_RequestTakeAll")]
        private static IEnumerable<CodeInstruction> RequestTakeAllTranspiler(IEnumerable<CodeInstruction> instructions) =>
            RedirectLogCalls(instructions, "RPC_RequestTakeAll");
    }
}
