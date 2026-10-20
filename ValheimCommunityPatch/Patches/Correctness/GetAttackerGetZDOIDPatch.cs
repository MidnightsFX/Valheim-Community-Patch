using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness
{
    [PatchSide(Side.Both)] // I think?
    [HarmonyPatch]
    internal static class GetAttackerGetZDOIDPatch
    {
        private static readonly MethodInfo HitData_GetAttacker = AccessTools.Method(typeof(HitData), nameof(HitData.GetAttacker));
        private static readonly MethodInfo Character_GetZDOID = AccessTools.Method(typeof(Character), nameof(Character.GetZDOID));
        private static readonly FieldInfo HitData_AttackerZDOID = AccessTools.Field(typeof(HitData), nameof(HitData.m_attacker));

        // Targets and replaces these two lines
        //IL_00fd: ldarg.2
        //IL_00fe: callvirt instance class Character HitData::GetAttacker()
        //IL_0103: callvirt instance valuetype ZDOID Character::GetZDOID()
        private static readonly CodeMatch[] GetAttackerGetZDOIDInstructions = new CodeMatch[] { 
            new CodeMatch(OpCodes.Callvirt, HitData_GetAttacker),
            new CodeMatch(OpCodes.Callvirt, Character_GetZDOID),
        };

        private static readonly List<CodeInstruction> GetAttackerZDOIDReplacementInstructions = new List<CodeInstruction>() { 
            new CodeInstruction(OpCodes.Ldfld, HitData_AttackerZDOID)
        };

        // In the future target and replace these lines as well
        //IL_0107: callvirt instance class Character HitData::GetAttacker()
        //IL_010c: ldnull
        //IL_010d: call bool[UnityEngine.CoreModule] UnityEngine.Object::op_Inequality(class [UnityEngine.CoreModule] UnityEngine.Object, class [UnityEngine.CoreModule] UnityEngine.Object)
        //IL_0112: brfalse.s IL_0120

        //private static readonly CodeMatch[] CheckAttackerIsNullInstructions = new CodeMatch[] {
        //    new CodeMatch(OpCodes.Callvirt, HitData_GetAttacker),
        //    new CodeMatch(OpCodes.Ldnull),
        //    new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object)))
        //};


        [HarmonyTranspiler]
        [HarmonyPatch(typeof(MineRock), nameof(MineRock.RPC_Hit))]
        private static IEnumerable<CodeInstruction> MineRock_RPC_Hit_GetAttackerZDOIDOverride(IEnumerable<CodeInstruction> instructions)
        {
            //Logger.LogInfo("Patching MineRock.RPC_Hit");
            return PatchHelper.ReplaceAllMatches(instructions, GetAttackerGetZDOIDInstructions, GetAttackerZDOIDReplacementInstructions, nameof(MineRock.RPC_Hit));
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(MineRock5), nameof(MineRock5.DamageArea))]
        private static IEnumerable<CodeInstruction> MineRock5_DamageArea_GetAttackerZDOIDOverride(IEnumerable<CodeInstruction> instructions)
        {
            return PatchHelper.ReplaceAllMatches(instructions, GetAttackerGetZDOIDInstructions, GetAttackerZDOIDReplacementInstructions, nameof(MineRock5.DamageArea));
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(TreeBase), nameof(TreeBase.RPC_Damage))]
        private static IEnumerable<CodeInstruction> TreeBase_RPC_Damage_GetAttackerZDOIDOverride(IEnumerable<CodeInstruction> instructions)
        {
            return PatchHelper.ReplaceAllMatches(instructions, GetAttackerGetZDOIDInstructions, GetAttackerZDOIDReplacementInstructions, nameof(TreeBase.RPC_Damage));
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(TreeLog), nameof(TreeLog.RPC_Damage))]
        private static IEnumerable<CodeInstruction> TreeLog_RPC_Damage_GetAttackerZDOIDOverride(IEnumerable<CodeInstruction> instructions)
        {
            return PatchHelper.ReplaceAllMatches(instructions, GetAttackerGetZDOIDInstructions, GetAttackerZDOIDReplacementInstructions, nameof(TreeLog.RPC_Damage));
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.RPC_Damage))]
        private static IEnumerable<CodeInstruction> WearNTear_RPC_Damage_GetAttackerZDOIDOverride(IEnumerable<CodeInstruction> instructions)
        {
            return PatchHelper.ReplaceAllMatches(instructions, GetAttackerGetZDOIDInstructions, GetAttackerZDOIDReplacementInstructions, nameof(WearNTear.RPC_Damage));
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Destroy))]
        private static IEnumerable<CodeInstruction> WearNTear_Destroy_GetAttackerZDOIDOverride(IEnumerable<CodeInstruction> instructions)
        {
            return PatchHelper.ReplaceAllMatches(instructions, GetAttackerGetZDOIDInstructions, GetAttackerZDOIDReplacementInstructions, nameof(WearNTear.Destroy));
        }
    }
}
