using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // An Aoe without trigger damage warns per contact every physics step; a Pufferfish on an item stand never stops.
    // Prefixes Aoe.CauseTriggerDamage to skip it when m_useTriggers is off, where vanilla's body is only that warning.
    // The game log stays clean; the line still goes to debug logging with EnableDebugMode on.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(Aoe))]
    internal static class AoeLogSpamPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(AoeLogSpamPatch),
                ValConfig.SectionCorrectness,
                "Fix Area Damage Log Spam",
                true,
                "Stops area damage objects without contact damage, such as a Pufferfish on an item stand, " +
                "flooding the game log with a warning for everything touching them.");
        }

        [HarmonyPrefix]
        [HarmonyPatch("CauseTriggerDamage")]
        private static bool Prefix(Aoe __instance) {
            if (__instance.m_useTriggers || Enabled == null || !Enabled.Value) { return true; }

            if (Logger.DebugEnabled) {
                Logger.DebugSink("AOE got OnTriggerStay but trigger damage is disabled in " + __instance.gameObject.name);
            }
            return false;
        }
    }
}
