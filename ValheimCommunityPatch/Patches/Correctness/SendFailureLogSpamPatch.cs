using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // ZSteamSocket logs "Failed to send data" per socket per frame while a peer's send queue is backed up.
    // Transpiles ZSteamSocket.SendQueuedPackages to send its ZLog.Log call to the debug sink.
    // A backed-up connection no longer floods the log just when the machine can least afford it.
    // Provenance: same defect as ComfyMods/BetterZeeLog (GPL-3.0, redseiko), which removes the call outright.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZSteamSocket))]
    internal static class SendFailureLogSpamPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SendFailureLogSpamPatch),
                ValConfig.SectionCorrectness,
                "Fix Send Failure Log Spam",
                true,
                "Stops 'Failed to send data' filling the game log every frame while a connection is backed up " +
                "(EnableDebugMode still shows it); turning it on needs a game restart.");
        }

        private static readonly MethodInfo ZLogMethod = AccessTools.Method(typeof(ZLog), nameof(ZLog.Log));
        private static readonly MethodInfo SinkMethod = AccessTools.Method(typeof(SendFailureLogSpamPatch), nameof(Sink));

        // The debug log, or the game's log again once the fix is turned off.
        private static void Sink(object message) {
            if (Enabled.Value) {
                Logger.DebugSink(message);
            } else {
                ZLog.Log(message);
            }
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches. Only the call operand changes, so the break
        // after it still ends the drain loop on the first failure.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("SendQueuedPackages")]
        private static IEnumerable<CodeInstruction> SendQueuedPackagesTranspiler(IEnumerable<CodeInstruction> instructions) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            return PatchHelper.ReplaceCalls(instructions, ZLogMethod, SinkMethod, "ZSteamSocket.SendQueuedPackages");
        }
    }
}
