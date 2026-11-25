using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Every chest open, stack-all and take-all writes a line or two to the game log on the container's owner.
    // Transpiles Container.RPC_RequestOpen, RPC_RequestStack and RPC_RequestTakeAll to log to the debug sink.
    // Using chests no longer floods the log; the lines come back with EnableDebugMode.
    // Provenance: same defect as ComfyMods/BetterZeeLog (GPL-3.0, redseiko), which removes the calls.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class ContainerLogSpamPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ContainerLogSpamPatch),
                ValConfig.SectionCorrectness,
                "Fix Container Log Spam",
                true,
                "Stops every chest open, stack-all and take-all writing lines to the game log (EnableDebugMode " +
                "still shows them); turning it on needs a game restart.");
        }

        private static readonly MethodInfo ZLogMethod = AccessTools.Method(typeof(ZLog), nameof(ZLog.Log));
        private static readonly MethodInfo SinkMethod = AccessTools.Method(typeof(ContainerLogSpamPatch), nameof(Sink));

        // The debug log, or the game's log again once the fix is turned off.
        private static void Sink(object message) {
            if (Enabled.Value) {
                Logger.DebugSink(message);
            } else {
                ZLog.Log(message);
            }
        }

        // Only the call is repointed: the message is still built, but the write and its stack trace are the bulk of the cost.
        private static IEnumerable<CodeInstruction> RedirectLogCalls(IEnumerable<CodeInstruction> instructions, string method) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            return PatchHelper.ReplaceCalls(instructions, ZLogMethod, SinkMethod, "Container." + method);
        }

        // Priority.Last on all three: see ValheimCommunityPatch.ApplyPatches. With BetterZeeLog installed its
        // removal lands first and this repoints whatever it left.
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
