using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Taming never clears the patrol point a spawner stamped on a creature, so a tame keeps running back to its spawn.
    // Postfixes MonsterAI.MakeTame to call vanilla's ResetPatrolPoint on the creature's owner.
    // A newly tamed creature stays where you keep it, boars above all, which cannot be told to follow or stay.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(MonsterAI))]
    internal static class TamedPatrolPointPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(TamedPatrolPointPatch),
                ValConfig.SectionCorrectness,
                "Clear Patrol Point On Taming",
                true,
                "Makes a newly tamed creature stay where you keep it instead of running back to where it spawned.");
        }

        // Cleared at taming rather than ignored in the idle path, because the "stay" command is a patrol point too.
        // Creatures tamed before this fix keep theirs, as do prefabs that start tamed and never pass through MakeTame.
        [HarmonyPostfix]
        [HarmonyPatch(nameof(MonsterAI.MakeTame))]
        private static void MakeTamePostfix(MonsterAI __instance) {
            if (Enabled == null || !Enabled.Value) { return; }

            // ResetPatrolPoint writes the ZDO without checking ownership. Vanilla's Tame is already
            // owner-gated; this guard covers a mod reaching MakeTame from somewhere that is not.
            ZNetView nview = __instance.m_nview;
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) { return; }

            __instance.ResetPatrolPoint();
        }
    }
}
