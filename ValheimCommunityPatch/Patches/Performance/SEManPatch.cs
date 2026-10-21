using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using System.Reflection.Emit;

namespace ValheimCommunityPatch.Patches.Performance
{
    [PatchSide(Side.Client)]
    [HarmonyPatch]
    internal static class SEManPatch
    {
        //private static readonly MethodInfo SEManConstructor = AccessTools.Method(typeof(SEMan), ".ctor");
        private static readonly Dictionary<SEMan, Dictionary<int, StatusEffect>> SEManTracker = new Dictionary<SEMan, Dictionary<int, StatusEffect>>();

        private static readonly FieldInfo RemoveStatusEffectsListField = AccessTools.Field(typeof(SEMan), nameof(SEMan.m_removeStatusEffects));
        private static readonly MethodInfo StatusEffectListClearMethod = AccessTools.Method(typeof(List<StatusEffect>), nameof(List<StatusEffect>.Clear));

        /// <summary>
        ///     Adds all new instances of SEMan to tracker upon construction
        /// </summary>
        /// <param name="instructions"></param>
        /// <returns></returns>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(SEMan), MethodType.Constructor, new[] { typeof(Character), typeof(ZNetView) })]
        internal static IEnumerable<CodeInstruction> SEMan_Tracker(IEnumerable<CodeInstruction> instructions)
        {
            IEnumerable<CodeInstruction> codes = PatchHelper.Copy(instructions);
            CodeMatcher matcher = new(codes);
            matcher.End(); // move to end and match backwards
            if (matcher.TryMatchStartBackwards(new[] { new CodeMatch(OpCodes.Ret) }))
            {
                matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Ldarg_0)).InsertAndAdvance(
                    Transpilers.EmitDelegate(AddSEManToTracker)
                );
                return matcher.InstructionEnumeration();
            }
            return instructions;
        }

        /// <summary>
        ///     Delegate method to add all SEMan instances to this tracker
        /// </summary>
        /// <param name="seMan"></param>
        private static void AddSEManToTracker(SEMan seMan)
        {
            if (!SEManTracker.ContainsKey(seMan))
            {
                SEManTracker.Add(seMan, new Dictionary<int, StatusEffect>());
            }
        }

        /// <summary>
        ///     Remove instance from tracker upon destruction
        /// </summary>
        /// <param name="__instance"></param>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.OnDestroy))]
        internal static void SEMan_Tracker_RemoveOnDestory(SEMan __instance)
        {
            if (SEManTracker.ContainsKey(__instance))
            {
                SEManTracker[__instance].Clear();
                SEManTracker.Remove(__instance);
            }
        }

        /// <summary>
        ///     Redirect requests to get status effects to the SEManTracker to increase performance
        ///     via dictionary lookups
        /// </summary>
        /// <param name="instructions"></param>
        /// <returns></returns>
        [HarmonyTranspiler]
        //[HarmonyEmitIL(".dumps")]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.GetStatusEffect), new[] { typeof(int) })]
        internal static IEnumerable<CodeInstruction> PatchGetStatusEffect(IEnumerable<CodeInstruction> instructions)
        {
            IEnumerable<CodeInstruction> codes = PatchHelper.Copy(instructions);
            CodeMatcher matcher = new(codes);
            CodeMatch[] matches = new[] { new CodeMatch(OpCodes.Ldarg_0) }; // targets the first line after the initial guard

            if (matcher.TryMatchStartForward(matches))
            {
                matcher.Advance(1).InsertAndAdvance(
                    new[] { new CodeInstruction(OpCodes.Ldarg_1) } // SEMan already loaded so just load input NameHash
                ).InsertAndAdvance(Transpilers.EmitDelegate(GetStatusEffectFromTracker)) // call method to check for effect from tracker
                .InsertAndAdvance(new CodeInstruction(OpCodes.Ret)); // return the result from the tracker
                
                return matcher.InstructionEnumeration();
            }

            return instructions;
        }

        /// <summary>
        ///     Delegate metod for getting status effect from tracker if the SEMan instance is being tracked.
        /// </summary>
        /// <param name="seMan"></param>
        /// <param name="nameHash"></param>
        /// <returns></returns>
        private static StatusEffect GetStatusEffectFromTracker(SEMan seMan, int nameHash)
        {
            if (SEManTracker.TryGetValue(seMan, out Dictionary<int, StatusEffect> effectMap) && effectMap.ContainsKey(nameHash)) { 
                return effectMap[nameHash];
            }
            return null;
        }

        /// <summary>
        ///     This targets the add method that SEMan.Internal_AddStatusEffect uses to actually add a new status effect. Does not
        ///     prevent adding status effects or hashes to SEMan.m_statusEffects or SEMan.m_statusEffectHashes as those need to remain
        ///     populated just as they would in Vanilla
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.AddStatusEffect), new[] { typeof(StatusEffect), typeof(bool), typeof(int), typeof(float), typeof(short) })]
        internal static void Patch_AddStatusEffect(SEMan __instance, ref StatusEffect __result)
        {
            if (!SEManTracker.ContainsKey(__instance) || !__result)
            {
                return;
            }
            SEManTracker[__instance].Add(__result.NameHash(), __result);
        }

        /// <summary>
        ///     Clear references to name hashes and status effects in tracker when removing them all from SEMan
        /// </summary>
        /// <param name="__instance"></param>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.RemoveAllStatusEffects))]
        internal static void UpdateTrackerOnRemoveAll(SEMan __instance)
        {
            if (!SEManTracker.ContainsKey(__instance))
            {
                return;
            }
            SEManTracker[__instance].Clear();
        }

        /// <summary>
        ///     Transpiler to redirect requests to remove a single status effect to a delegate method that uses 
        ///     the more performannt SEManTracker dictionary look up
        /// </summary>
        /// <param name="instructions"></param>
        /// <returns></returns>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.RemoveStatusEffect), new[] { typeof(int), typeof(bool) })]
        internal static IEnumerable<CodeInstruction> PatchRemoveStatusEffect(IEnumerable<CodeInstruction> instructions)
        {
            IEnumerable<CodeInstruction> codes = PatchHelper.Copy(instructions);
            CodeMatcher matcher = new(codes);
            try
            {
                matcher.Start();  // insert code at the start to bypass the entire method
                return matcher.InsertAndAdvance(
                    new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Ldarg_2) } // load SEMan, input NameHash, and quiet flag
                ).InsertAndAdvance(Transpilers.EmitDelegate(RemoveStatusEffectFromTracker)) // call method to check for effect in tracker
                .InsertAndAdvance(new CodeInstruction(OpCodes.Ret)) // return the result from the tracker
                .InstructionEnumeration(); // return instructions
            }
            catch (Exception e) 
            {
                    Logger.LogWarning($"{e.Message}");
            }
            return instructions;
        }

        /// <summary>
        ///     Delegate method to remove a single status effect from tracker if the SEMan instance is being tracked.
        /// </summary>
        /// <param name="seMan"></param>
        /// <param name="nameHash"></param>
        /// <returns></returns>
        private static bool RemoveStatusEffectFromTracker(SEMan seMan, int nameHash, bool quiet)
        {
            if (nameHash == 0)
            {
                return false;
            }
            if (SEManTracker.TryGetValue(seMan, out Dictionary<int, StatusEffect> effectMap) && effectMap.TryGetValue(nameHash, out StatusEffect statusEffect))
            {
                if (quiet)
                {
                    statusEffect.m_stopMessage = "";
                }
                statusEffect.Stop();
                effectMap.Remove(nameHash);
                seMan.m_statusEffectsHashSet.Remove(nameHash);
                seMan.m_statusEffects.Remove(statusEffect); // don't love this line performance-wise but it works
            }
            return false;
        }

        /// <summary>
        ///     Transpiler to run delegate method for updating the SEManTracker just before clearing the list
        ///     of status effects that should be removed from SEMan.m_removeStatusEffects
        /// </summary>
        /// <param name="instructions"></param>
        /// <returns></returns>
        [HarmonyTranspiler]
        [HarmonyEmitIL(".dumps")]
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.Update))]
        internal static IEnumerable<CodeInstruction> Patch_SEMan_Update(IEnumerable<CodeInstruction> instructions)
        {
            // Targets this IL Code
            // ldarg.0
            // IL_00c3: ldfld class [mscorlib] System.Collections.Generic.List`1<class StatusEffect> SEMan::m_removeStatusEffects
            // IL_00c8: callvirt instance void class [mscorlib] System.Collections.Generic.List`1<class StatusEffect>::Clear()

            IEnumerable<CodeInstruction> codes = PatchHelper.Copy(instructions);
            CodeMatcher matcher = new(codes);
            
            CodeMatch[] matches = new[] { 
                new CodeMatch(OpCodes.Ldarg_0),
                new CodeMatch(OpCodes.Ldfld, RemoveStatusEffectsListField),
                new CodeMatch(OpCodes.Callvirt, StatusEffectListClearMethod),
            };

            matcher.End();
            if (matcher.TryMatchStartBackwards("Failed to find anchor instructions for SEMan.Update Transpiler", matches))
            {
                return matcher.InsertAndAdvance(new[] {
                    new CodeInstruction(OpCodes.Ldarg_0),  // load SEMan onto stack
                }).InsertAndAdvance(Transpilers.EmitDelegate(RemoveStatusEffectsFromTrackerOnUpdate)) // call delegate with SEMan instance
                .InstructionEnumeration();
            }
            
            return instructions;
        }

        /// <summary>
        ///     During SEMan.Update a bunch of StatusEffects may be stopped and removed. Need to update the tracker accordingly.
        /// </summary>
        /// <param name="seMan"></param>
        /// <returns></returns>
        private static void RemoveStatusEffectsFromTrackerOnUpdate(SEMan seMan)
        {
            if (!SEManTracker.TryGetValue(seMan, out Dictionary<int, StatusEffect> effectMap) || seMan.m_removeStatusEffects.Count < 1)
            {
                return;
            }

            int nameHash;
            foreach (StatusEffect removeEffect in seMan.m_removeStatusEffects)
            {
                nameHash = removeEffect.NameHash();
                if (effectMap.ContainsKey(nameHash))
                {
                    effectMap.Remove(nameHash); 
                }   
            }
            return;
        }
        
    }
}
