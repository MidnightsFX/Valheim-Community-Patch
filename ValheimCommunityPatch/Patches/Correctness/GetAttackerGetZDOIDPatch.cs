using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using System.Reflection.Emit;


namespace ValheimCommunityPatch.Patches.Correctness
{
    [PatchSide(Side.Both)] // I think?
    [HarmonyPatch]
    internal static class GetAttackerGetZDOIDPatch
    {
        // Targets and replaces these two lines
        //IL_00fe: callvirt instance class Character HitData::GetAttacker()
        //IL_0103: callvirt instance valuetype ZDOID Character::GetZDOID()
        private static CodeMatch[] GetAttackerGetZDOIDInstructions = new CodeMatch[] { 
            new CodeMatch(OpCodes.Callvirt, AccessTools.Method(typeof(HitData), nameof(HitData.GetAttacker))),
            new CodeMatch(OpCodes.Callvirt, AccessTools.Method(typeof(Character), nameof(Character.GetZDOID))),
        };

        private static List<CodeInstruction> ReplacementInstructions = new List<CodeInstruction>() { 
            new CodeInstruction(OpCodes.Ldflda, AccessTools.Field(typeof(HitData), nameof(HitData.m_attacker)))
        };

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(MineRock), nameof(MineRock.RPC_Hit))]
        private static IEnumerable<CodeInstruction> MineRockGetAttackerOverride(IEnumerable<CodeInstruction> instructions)
        {
            return PatchHelper.ReplaceAllMatches(instructions, GetAttackerGetZDOIDInstructions, ReplacementInstructions, nameof(MineRock.RPC_Hit));
        }
    }
}
