using System.Collections.Generic;
using HarmonyLib;
using System.Reflection.Emit;

namespace ValheimCommunityPatch.Patches.Performance
{
    [PatchSide(Side.Client)]
    [HarmonyPatch]
    internal static class ObjectDBStatusEffectPatch
    {
        //private static readonly MethodInfo SEManConstructor = AccessTools.Method(typeof(SEMan), ".ctor");
        private static readonly Dictionary<int, StatusEffect> StatusEffectMap = new Dictionary<int, StatusEffect>();

        /// <summary>
        ///     Reset and update the map of status effects whenever ObjectDB is updated
        /// </summary>
        /// <param name="__instance"></param>
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)] // want to do this after other mods add new status effects
        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.UpdateRegisters))]
        internal static void UpdateStatusEffectMap(ObjectDB __instance)
        {
            StatusEffectMap.Clear();
            int nameHash;
            foreach (StatusEffect statusEffect in __instance.m_StatusEffects)
            {
                nameHash = statusEffect.NameHash();
                if (!StatusEffectMap.ContainsKey(nameHash))
                {
                    StatusEffectMap.Add(nameHash, statusEffect);    
                }
            }
        }

        /// <summary>
        ///     Redirect request for status effect to delegate method and if it returns null then run the original 
        ///     method in case someone directly modified ObjectDB.m_statusEffects without changing the total
        ///     number of status effects in the list.
        /// </summary>
        /// <param name="instructions"></param>
        /// <param name="il"></param>
        /// <returns></returns>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.GetStatusEffect))]
        internal static IEnumerable<CodeInstruction> ObjectDB_GetStatusEffect(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            IEnumerable<CodeInstruction> codes = PatchHelper.Copy(instructions);
            CodeMatcher matcher = new(codes);
            Label originalMethodStart = il.DefineLabel();
            matcher.Start();
            return matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Ldarg_1)) // load the nameHash the original method was called with
                .InsertAndAdvance(Transpilers.EmitDelegate(GetStatusEffectFromMap)) // call delegate with nameHash and return result
                .InsertAndAdvance(new[]
                {
                    new CodeInstruction(OpCodes.Dup), // duplicate the returned value to use it in the Brfalse statment 
                    new CodeInstruction(OpCodes.Brfalse, originalMethodStart), // break to original method start if result from status effect was null 
                    new CodeInstruction(OpCodes.Ret)  // return the result
                }).AddLabels(new List<Label>() { originalMethodStart })  // add label for Brfalse to jump to just after the return statement
                .InsertAndAdvance(new CodeInstruction(OpCodes.Pop)) // pop the value returned by GetStatusEffectFromMap before running original method
                .InstructionEnumeration();
        }


        /// <summary>
        ///     Check if StatusEffectMap is still current and update the map if it is not
        ///     because something directly modified the number of items in ObjectDB.m_statusEffects
        /// </summary>
        /// <param name="nameHash"></param>
        /// <returns></returns>
        internal static StatusEffect GetStatusEffectFromMap(int nameHash) {

            if (ObjectDB.instance.m_StatusEffects.Count != StatusEffectMap.Count)
            {
                UpdateStatusEffectMap(ObjectDB.instance);
            }

            if (StatusEffectMap.TryGetValue(nameHash, out StatusEffect statusEffect)){
                return statusEffect;
            }

            return null;
        }
    }
}
