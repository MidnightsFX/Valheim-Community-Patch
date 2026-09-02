using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Run Attack Stamina Drain: stops run stamina draining while a player is mid-attack.
    //
    // Vanilla halts movement during an attack, but Character.CheckRun does not know about
    // attacks, so Player.CheckRun keeps charging run stamina for the whole swing.
    //
    // A postfix on Character.CheckRun returns false for a Player that is attacking. It targets the
    // base method because Player.CheckRun charges stamina after the base check passes, so a
    // postfix on Player.CheckRun would run too late. Narrowed to Player so creature AI is
    // unchanged.
    //
    // Client: a Player's CheckRun only runs on its owning client.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(Character))]
    internal static class RunAttackStaminaPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(RunAttackStaminaPatch),
                ValConfig.SectionCorrectness,
                "Fix Run Attack Stamina Drain",
                true,
                "Stops run stamina draining while you are mid-attack. Vanilla halts your movement during a " +
                "swing but keeps charging sprint stamina for its whole duration.");
        }

        [HarmonyPostfix]
        [HarmonyPatch("CheckRun")]
        private static void CheckRunPostfix(Character __instance, ref bool __result) {
            if (Enabled == null || !Enabled.Value) { return; }
            if (!__result) { return; }
            if (!(__instance is Player)) { return; }

            if (__instance.InAttack()) { __result = false; }
        }
    }
}
