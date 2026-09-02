using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Send Failure Log Spam: stops "Failed to send data" being written once per socket per
    // frame while a peer's send queue is backed up.
    //
    // ZSteamSocket.SendQueuedPackages logs every failed send, and it runs from Update for every
    // socket every frame. A send only fails when the peer's queue has backed up, which is exactly
    // when the machine can least afford to format lines, capture stack traces and write to disk.
    //
    // A transpiler points the ZLog.Log call at this mod's debug sink, so the message is silenced by
    // default and comes back with EnableDebugMode. Only the call operand changes; the break that
    // follows it still stops the drain loop on the first failure.
    //
    // Both, and worse on a server, which holds one socket per connected peer. Provenance: same
    // defect as ComfyMods/BetterZeeLog (GPL-3.0, redseiko), which removes the call outright.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ZSteamSocket))]
    internal static class SendFailureLogSpamPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SendFailureLogSpamPatch),
                ValConfig.SectionCorrectness,
                "Fix Send Failure Log Spam",
                true,
                "Stops 'Failed to send data' being written to the game log once per socket per frame " +
                "whenever a peer's send queue backs up - which is exactly when the server can least " +
                "afford it. The message is still visible with EnableDebugMode on. Changing this " +
                "requires a game restart.");
        }

        private static readonly MethodInfo ZLogMethod = AccessTools.Method(typeof(ZLog), nameof(ZLog.Log));
        private static readonly MethodInfo SinkMethod = AccessTools.Method(typeof(Logger), nameof(Logger.DebugSink));

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("SendQueuedPackages")]
        private static IEnumerable<CodeInstruction> SendQueuedPackagesTranspiler(IEnumerable<CodeInstruction> instructions) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            return PatchHelper.ReplaceCalls(instructions, ZLogMethod, SinkMethod, "ZSteamSocket.SendQueuedPackages");
        }
    }
}
