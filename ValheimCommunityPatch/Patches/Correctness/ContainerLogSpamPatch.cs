using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Vanilla defect: the three container request RPCs log unconditionally, then log again on every
    // rejection path. Container.RPC_RequestOpen, verbatim:
    //
    //   ZLog.Log((object) $"Player {uid} wants to open {this.gameObject.name}   im: {ZDOMan.GetSessionID()}");
    //   if (!this.m_nview.IsOwner())
    //     ZLog.Log((object) "  but im not the owner");
    //   else if (...) { ZLog.Log((object) "  in use"); ... }
    //   else if (!this.CheckAccess(playerID)) { ZLog.Log((object) "  not yours"); ... }
    //
    // RPC_RequestStack and RPC_RequestTakeAll are the same shape. Every chest, every open, stack-all
    // and take-all - and the leading line is built whether anything interesting happened or not,
    // marshalling a fresh string out of native Unity for gameObject.name on top of the interpolation.
    //
    // These land on the container's *owner*, not on everyone: ZNetView.InvokeRPC addresses
    // m_zdo.GetOwner(), and a server relaying a peer-targeted RPC never invokes the handler itself.
    // So a player sorting their own base logs their own chests, and a server logs the containers it
    // happens to own. Either way it is a steady stream of lines nobody reads.
    //
    // Fix: route the messages to this mod's debug log instead of the game's.
    //
    // Scope note: repointing the call cannot elide the string building that feeds it - the argument
    // is already on the stack by then. That is the deliberate trade. The log write and its stack
    // trace are the bulk of the cost, and an operand swap survives these methods changing, whereas
    // unpicking a DefaultInterpolatedStringHandler sequence is exactly the kind of fragile IL
    // matching this project avoids elsewhere.
    //
    // Provenance: same defect as ComfyMods/BetterZeeLog (GPL-3.0, redseiko), which NOPs the calls out
    // and branches over the leading statement. Redirected rather than deleted here so the messages
    // are still recoverable by turning on EnableDebugMode.
    //
    // These three methods are shared with that mod, so the transpilers run at Priority.Last for the
    // reason set out in ValheimCommunityPatch.ApplyPatches: it matches on the ZLog.Log operand we
    // rewrite, and throws rather than backing off when it cannot find it. Running after it, we find
    // only the leading call it branched over, repoint that, and leave its stronger fix in place.
    //
    // Both: whoever owns the container does the logging, and that is a client for most chests but the
    // server for any it owns.
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
        private static readonly MethodInfo SinkMethod =
            AccessTools.Method(typeof(ContainerLogSpamPatch), nameof(LogDebugSink));

        // Signature matches ZLog.Log(object), so this is a drop-in replacement for the call.
        private static void LogDebugSink(object message) {
            if (Logger.Level < BepInEx.Logging.LogLevel.Debug) { return; }

            Logger.LogDebug(message?.ToString() ?? string.Empty);
        }

        private static IEnumerable<CodeInstruction> RedirectLogCalls(IEnumerable<CodeInstruction> instructions, string method) {
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
                    $"Container.{method}: found no ZLog.Log calls to redirect, so this fix is inactive here. " +
                    "Another mod has most likely already rewritten the method - if so, nothing is wrong.");
                return instructions;
            }

            return codes;
        }

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
