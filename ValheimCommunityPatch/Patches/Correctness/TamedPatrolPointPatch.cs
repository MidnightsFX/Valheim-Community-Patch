using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Clear Patrol Point On Taming: clears the patrol point a spawner stamped on a creature at the
    // moment it is tamed, so it settles where you keep it instead of walking back to where it spawned.
    //
    // SpawnArea, CreatureSpawner and TriggerSpawner call BaseAI.SetPatrolPoint on the creatures they
    // instantiate, which persists the spawn position and a patrol flag into the creature's ZDO for
    // life. Taming never clears it: Tameable.Tame calls MonsterAI.MakeTame, which sets the tame flag
    // and drops alert and targets but leaves patrol state alone. BaseAI.IdleMovement then overwrites
    // the tamed "roam around where I am" centre with the patrol point unconditionally, and
    // RandomMovement runs the animal home whenever it drifts past twice its roam range.
    // MonsterAI.UpdateTarget also gives up on a target by its distance from the patrol point rather
    // than from whoever the creature is following, so it will not defend you away from that spot.
    // Boars show it worst: they are not commandable, so the follow command that would otherwise call
    // ResetPatrolPoint is unreachable and the point is theirs permanently.
    //
    // A postfix on MakeTame calls vanilla's own ResetPatrolPoint, on the owner only, since that
    // writes the ZDO unguarded. Clearing at the moment of taming rather than ignoring patrol in the
    // idle path is deliberate: a patrol point is also how the "stay" command is implemented, in
    // Tameable.RPC_Command, so suppressing it for tamed creatures would stop wolves and lox staying
    // put. Not covered: creatures already tamed before this fix, which keep the stale point, and
    // m_startsTamed prefabs, which reach Character.SetTamed from Tameable.Awake without passing
    // through MakeTame, where a patrol point is plausibly what the prefab intended.
    //
    // Both: MakeTame runs on the ZDO owner, usually the taming player's client, but a dedicated
    // server owns the creatures in its own active area and reaches the same path through the tame
    // console command.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(MonsterAI))]
    internal static class TamedPatrolPointPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(TamedPatrolPointPatch),
                ValConfig.SectionCorrectness,
                "Clear Patrol Point On Taming",
                true,
                "Clears the patrol point a spawner stamped on a creature when you tame it, so it stays " +
                "where you keep it instead of running back to where it spawned. Mostly affects boars, " +
                "which cannot be told to follow or stay and so have no way to clear it. Telling a tamed " +
                "creature to stay still sets a patrol point as normal.");
        }

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
