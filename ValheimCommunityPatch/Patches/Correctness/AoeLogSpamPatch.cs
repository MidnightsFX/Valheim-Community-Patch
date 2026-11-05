using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Area Damage Log Spam: stops area damage objects that do not use contact damage writing a warning
    // to the game log for everything that touches them.
    //
    // Aoe.CauseTriggerDamage runs from OnCollisionEnter, OnCollisionStay, OnTriggerEnter and OnTriggerStay.
    // With m_useTriggers off it does nothing but warn, once per contact per physics step. Most such objects
    // last seconds. The Pufferfish keeps one on its model for good, with a solid collider, and on an item
    // stand there is no rigidbody above it to take the physics messages instead, so a player or a pile of
    // dropped items touching the fish writes the warning continuously.
    //
    // A prefix skips the method when m_useTriggers is off, which skips only the warning and the string built
    // for it. The line is still written with EnableDebugMode on.
    //
    // Both: any machine simulating the object can log it.
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
                "Stops area damage objects that do not use contact damage writing a warning to the game log " +
                "for everything touching them, every physics step. A Pufferfish on an item stand kept this " +
                "going for as long as a player or dropped items touched it. The line is still visible with " +
                "EnableDebugMode on.");
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
