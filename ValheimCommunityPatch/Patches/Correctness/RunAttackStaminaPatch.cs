using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Vanilla defect: holding sprint while attacking stops the player moving, but the run check does
    // not know about attacks, so run stamina keeps draining for the whole swing.
    //
    //   Character.CheckRun (the base virtual):
    //     return this.m_run && moveDir.magnitude >= 0.1 && !IsCrouching() && !IsEncumbered() && !InDodge();
    //
    //   Player.CheckRun:
    //     if (!base.CheckRun(moveDir, dt)) return false;      // <- early out, before the drain
    //     ...
    //     this.UseStamina(dt * drain * Game.m_moveStaminaRate);
    //
    // The patch has to target the *base* method. Patching Player.CheckRun instead would run after
    // UseStamina had already been called, which is exactly the cost we are trying to avoid.
    //
    // Scope note: Character.CheckRun is the virtual base for every character, so an unconditional
    // postfix would also stop creatures running mid-attack - a creature-AI change, not a bug fix.
    // This deliberately narrows to players.
    [HarmonyPatch(typeof(Character))]
    internal static class RunAttackStaminaPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindServerConfig(
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
