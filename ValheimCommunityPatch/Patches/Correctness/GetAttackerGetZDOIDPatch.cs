using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Rock Hit Crash: stops a hit on a leviathan, meteorite or other multi-part rock failing
    // halfway through, and leaving that part unbreakable, when the hit has no attacker this
    // machine can find.
    //
    // MineRock.RPC_Hit passes hit.GetAttacker().GetZDOID() to its hit and destroyed effects with no
    // null check. GetAttacker is null for a hit that carries no attacker and for an attacker the
    // rock's owner has not loaded, so a damaging hit throws after the new health is saved: on the
    // killing blow the part is never hidden and drops nothing, and every later hit stops at
    // "Already destroyed". MineRock5.DamageArea, TreeBase.RPC_Damage, TreeLog.RPC_Damage,
    // WearNTear.RPC_Damage and WearNTear.Destroy guard the same expression, but look the attacker
    // up a second time for it.
    //
    // Transpilers replace each GetAttacker().GetZDOID() pair with a call that returns
    // hit.m_attacker, the id GetZDOID returns whenever GetAttacker finds the attacker, so the
    // guarded sites skip the second lookup and MineRock no longer throws. The id only tells the
    // effect whose gamepad to rumble, so passing it for an attacker that is not loaded is harmless.
    //
    // Both: the handlers run on the hit object's owner. Provenance: contributed by searica (PR #21).
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class GetAttackerGetZDOIDPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(GetAttackerGetZDOIDPatch),
                ValConfig.SectionCorrectness,
                "Fix Rock Hit Crash",
                true,
                "Stops a hit on a leviathan, meteorite or other multi-part rock failing halfway and " +
                "leaving that part unbreakable when the hit comes from a mod, or from a player the " +
                "rock's owner has not loaded yet. Changing this requires a game restart.");
        }

        private static readonly MethodInfo GetAttackerMethod = AccessTools.Method(typeof(HitData), nameof(HitData.GetAttacker));
        private static readonly MethodInfo GetZdoidMethod = AccessTools.Method(typeof(Character), nameof(Character.GetZDOID));
        private static readonly MethodInfo AttackerIdMethod = AccessTools.Method(typeof(GetAttackerGetZDOIDPatch), nameof(AttackerId));

        // hit.GetAttacker().GetZDOID(), with the HitData already on the stack.
        private static readonly CodeMatch[] GetAttackerZdoid = {
            new CodeMatch(OpCodes.Callvirt, GetAttackerMethod),
            new CodeMatch(OpCodes.Callvirt, GetZdoidMethod),
        };

        private static readonly CodeInstruction[] AttackerIdCall = {
            new CodeInstruction(OpCodes.Call, AttackerIdMethod),
        };

        // The stored attacker id, or the game's own expression again once the fix is turned off.
        private static ZDOID AttackerId(HitData hit) =>
            Enabled.Value ? hit.m_attacker : hit.GetAttacker().GetZDOID();

        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, string site) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            // Checked here because a pattern built from a null method would match any callvirt.
            if (GetAttackerMethod == null || GetZdoidMethod == null || AttackerIdMethod == null) {
                Logger.LogWarning($"{site}: a method this fix needs could not be resolved, so it is inactive here.");
                return instructions;
            }

            return PatchHelper.ReplaceAllMatches(instructions, GetAttackerZdoid, AttackerIdCall, site);
        }

        // Priority.Last on all six: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(MineRock), nameof(MineRock.RPC_Hit))]
        private static IEnumerable<CodeInstruction> MineRockHitTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Rewrite(instructions, "MineRock.RPC_Hit");

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(MineRock5), nameof(MineRock5.DamageArea))]
        private static IEnumerable<CodeInstruction> MineRock5DamageAreaTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Rewrite(instructions, "MineRock5.DamageArea");

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(TreeBase), nameof(TreeBase.RPC_Damage))]
        private static IEnumerable<CodeInstruction> TreeBaseDamageTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Rewrite(instructions, "TreeBase.RPC_Damage");

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(TreeLog), nameof(TreeLog.RPC_Damage))]
        private static IEnumerable<CodeInstruction> TreeLogDamageTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Rewrite(instructions, "TreeLog.RPC_Damage");

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.RPC_Damage))]
        private static IEnumerable<CodeInstruction> WearNTearDamageTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Rewrite(instructions, "WearNTear.RPC_Damage");

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Destroy))]
        private static IEnumerable<CodeInstruction> WearNTearDestroyTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Rewrite(instructions, "WearNTear.Destroy");
    }
}
