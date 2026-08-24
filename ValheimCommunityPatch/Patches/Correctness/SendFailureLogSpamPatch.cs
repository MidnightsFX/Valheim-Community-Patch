using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Vanilla defect: ZSteamSocket.SendQueuedPackages logs every failed send, and it is called from
    // Update for every socket every frame:
    //
    //   else {
    //     ZLog.Log((object) ("Failed to send data " + connection.ToString()));
    //     break;
    //   }
    //
    // A send only fails when the peer's queue has backed up - which is precisely when the server is
    // already struggling. So the failure path produces a formatted line per socket per frame at the
    // worst possible moment. Logging is not free: Unity captures a managed stack trace for every
    // Debug.Log and the line is written to disk, so the spam deepens the stall that caused it.
    //
    // Fix: route the message to this mod's debug log instead of the game's. Same class of defect as
    // Fix Projectile Rotation Spam, and handled the same way.
    //
    // Provenance: same defect as ComfyMods/BetterZeeLog (GPL-3.0, redseiko), which NOPs the call out
    // entirely. Redirected rather than deleted here so the message is still recoverable by turning on
    // EnableDebugMode - stopping the spam should not mean destroying the diagnostic.
    //
    // Both, and worse on the server: ZSteamSocket.Update flushes the queue on every socket, and a
    // server holds one per connected peer where a client holds one.
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
        private static readonly MethodInfo SinkMethod =
            AccessTools.Method(typeof(SendFailureLogSpamPatch), nameof(LogDebugSink));

        // Signature matches ZLog.Log(object), so this is a drop-in replacement for the call: the
        // message is already on the stack and neither returns anything.
        private static void LogDebugSink(object message) {
            if (Logger.Level < BepInEx.Logging.LogLevel.Debug) { return; }

            Logger.LogDebug(message?.ToString() ?? string.Empty);
        }

        // Only the call is repointed. The `break` that follows it stays exactly where it was, so the
        // drain loop still stops on the first failure rather than spinning on a blocked socket.
        //
        // Priority.Last for the reason in ValheimCommunityPatch.ApplyPatches: BetterZeeLog shares this
        // method and matches on the ZLog.Log operand we rewrite, throwing rather than backing off.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("SendQueuedPackages")]
        private static IEnumerable<CodeInstruction> SendQueuedPackagesTranspiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            if (Enabled == null || !Enabled.Value) { return codes; }

            int patched = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (!codes[i].Calls(ZLogMethod)) { continue; }

                codes[i].operand = SinkMethod;
                patched++;
            }

            if (patched == 0) {
                Logger.LogWarning(
                    "ZSteamSocket.SendQueuedPackages: found no ZLog.Log call to redirect, so this fix is " +
                    "inactive. Another mod has most likely already rewritten the method - if so, nothing " +
                    "is wrong.");
                return instructions;
            }

            return codes;
        }
    }
}
