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
        //IL_00fd: ldarg.2
        //IL_00fe: callvirt instance class Character HitData::GetAttacker()
        //IL_0103: callvirt instance valuetype ZDOID Character::GetZDOID()
        private static readonly CodeMatch[] GetAttackerGetZDOIDInstructions = new CodeMatch[] { 
            new CodeMatch(OpCodes.Callvirt, AccessTools.Method(typeof(HitData), nameof(HitData.GetAttacker))),
            new CodeMatch(OpCodes.Callvirt, AccessTools.Method(typeof(Character), nameof(Character.GetZDOID))),
        };

        private static readonly List<CodeInstruction> ReplacementInstructions = new List<CodeInstruction>() { 
            new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(HitData), nameof(HitData.m_attacker)))
        };

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(MineRock), nameof(MineRock.RPC_Hit))]
        private static IEnumerable<CodeInstruction> MineRockGetAttackerOverride(IEnumerable<CodeInstruction> instructions)
        {
            Logger.LogInfo("Patching MineRock.RPC_Hit");
            return PatchHelper.ReplaceAllMatches(instructions, GetAttackerGetZDOIDInstructions, ReplacementInstructions, nameof(MineRock.RPC_Hit));
        }
    }
}
