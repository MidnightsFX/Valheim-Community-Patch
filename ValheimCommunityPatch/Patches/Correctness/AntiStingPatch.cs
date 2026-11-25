using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Status effects exist only in the player's own game, so a Deathsquito run by another game ignores the concoction.
    // SE_Stats.Setup postfix and Stop prefix count it on the player's ZDO; a PheromoneFleeCheck postfix reads it.
    // Deathsquitos flee a player under the Anti-Sting Concoction whichever game runs their AI.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class AntiStingPatch {
        private const string KeyPrefix = "VCP_PheromoneFlee_";

        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(AntiStingPatch),
                ValConfig.SectionCorrectness,
                "Fix Anti-Sting Concoction",
                true,
                "Makes Deathsquitos flee a player under the Anti-Sting Concoction whichever game runs their " +
                "AI; the player who drank it and the player running the Deathsquito both need the mod.");
        }

        private static readonly Dictionary<string, int> Keys = new Dictionary<string, int>();

        private static int KeyFor(string creatureName) {
            if (!Keys.TryGetValue(creatureName, out int key)) {
                key = (KeyPrefix + creatureName).GetStableHashCode();
                Keys[creatureName] = key;
            }
            return key;
        }

        // Counted, not flagged, so two effects naming one creature cannot clear each other.
        private static void Publish(SE_Stats effect, Character character, int change) {
            if (!Enabled.Value || !effect.m_pheromoneFlee || effect.m_pheromoneTarget == null || !(character is Player)) { return; }

            ZNetView view = character.m_nview;
            if (view == null || !view.IsValid() || !view.IsOwner()) { return; }

            Character target = effect.m_pheromoneTarget.GetComponent<Character>();
            if (target == null) { return; }

            ZDO zdo = view.GetZDO();
            int key = KeyFor(target.m_name);
            zdo.Set(key, System.Math.Max(0, zdo.GetInt(key) + change));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SE_Stats), nameof(SE_Stats.Setup))]
        private static void AfterSetup(SE_Stats __instance, Character character) => Publish(__instance, character, 1);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.Stop))]
        private static void BeforeStop(StatusEffect __instance) {
            if (__instance is SE_Stats stats) { Publish(stats, stats.m_character, -1); }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MonsterAI), "PheromoneFleeCheck")]
        private static void ReadCount(MonsterAI __instance, Character target, ref bool __result) {
            if (__result || !Enabled.Value || !(target is Player)) { return; }

            ZNetView view = target.m_nview;
            if (view == null || !view.IsValid() || view.IsOwner()) { return; }

            __result = view.GetZDO().GetInt(KeyFor(__instance.m_character.m_name)) > 0;
        }
    }
}
