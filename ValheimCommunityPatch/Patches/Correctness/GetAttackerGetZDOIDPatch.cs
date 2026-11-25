using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // MineRock.RPC_Hit throws after saving damage when it cannot find the attacker, leaving that rock part unbreakable.
    // Transpilers on six hit and damage handlers replace GetAttacker().GetZDOID() with the stored hit.m_attacker.
    // Multi-part rocks always break, and the tree, log and building handlers skip a second attacker lookup.
    // Provenance: contributed by searica (PR #21).
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
                "Stops hits from mods, or from players the rock's owner has not loaded, leaving part of a " +
                "leviathan, meteorite or other multi-part rock unbreakable; turning it on needs a game restart.");
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

        // The stored attacker id, which GetZDOID returns whenever GetAttacker finds the attacker. It only
        // picks whose gamepad the effect rumbles, so an unloaded attacker's id is harmless. Turned off,
        // the game's own expression again.
        private static ZDOID AttackerId(HitData hit) =>
            Enabled.Value ? hit.m_attacker : hit.GetAttacker().GetZDOID();

        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, string site) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            // Checked here because a pattern built from a null method would match any callvirt.
            if (GetAttackerMethod == null || GetZdoidMethod == null || AttackerIdMethod == null) {
                PatchHelper.WarnOnce($"{site}: a method this fix needs could not be resolved, so it is inactive here.");
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
