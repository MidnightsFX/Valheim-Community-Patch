using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Shield Skill Credit: gives the skill a Staff of Protection shield raises when it breaks
    // to the player who cast it, not to whoever was wearing it.
    //
    // The staff's area hit applies the shield status effect (SE_Shield) to each character in range,
    // and Character.RPC_Damage hands the effect the caster through StatusEffect.SetAttacker, which
    // SE_Shield does not override, so the caster is dropped. When the shield breaks, SE_Shield.IsDone
    // raises m_levelUpSkillOnBreak (Blood Magic) on the wearer's own Skills. A mage shielding a friend
    // levels the friend's Blood Magic, and a shield on a tamed creature, which has no Skills, levels
    // nobody's.
    //
    // A postfix on SetAttacker records the casting player's peer for each shield. When IsDone sees a
    // broken shield with a recorded caster, a prefix clears m_levelUpSkillOnBreak for that one call,
    // so vanilla still plays the break effects but raises nothing, and a postfix raises the caster's
    // skill by the same factor: directly when the caster is on this machine, otherwise through a
    // routed RPC to the caster's client. A finalizer puts the field back. A shield with no recorded
    // caster, or worn by the player on this machine who also cast it, is left to vanilla.
    //
    // Both: IsDone runs on whichever side owns the wearer, a dedicated server for a creature it
    // simulates, and the caster's client applies the credit. A caster without the mod gets nothing.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class ShieldSkillCreditPatch {
        internal static FixToggle Enabled;

        private const string RpcName = "VCP_ShieldBreakSkill";

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ShieldSkillCreditPatch),
                ValConfig.SectionCorrectness,
                "Fix Shield Skill Credit",
                true,
                "Gives the Blood Magic a Staff of Protection shield earns when it breaks to the player " +
                "who cast it. Vanilla gives it to whoever wore the shield, so shielding a friend levels " +
                "their Blood Magic instead of yours, and shielding a tamed creature levels nobody's. The " +
                "caster and the shielded player both need the mod.");
        }

        private sealed class Caster {
            internal long Peer;
        }

        // Keyed by the shield's per-character clone; an entry goes when its shield is collected.
        private static readonly ConditionalWeakTable<SE_Shield, Caster> Casters =
            new ConditionalWeakTable<SE_Shield, Caster>();

        private static readonly ConditionalWeakTable<SE_Shield, Caster>.CreateValueCallback NewCaster =
            _ => new Caster();

        private struct Redirect {
            internal bool Active;
            internal long Peer;
            internal Skills.SkillType Skill;
        }

        // Game.Start is where vanilla registers its own routed RPCs. Guarded because
        // ZRoutedRpc.Register throws on a second registration.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Game), nameof(Game.Start))]
        private static void GameStartPostfix() {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null) { return; }
            if (rpc.m_functions.ContainsKey(RpcName.GetStableHashCode())) { return; }

            rpc.Register<int, float>(RpcName, RPC_ShieldBreakSkill);
        }

        // SE_Shield inherits the empty base method, so this is the only place its caster appears.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.SetAttacker))]
        private static void SetAttackerPostfix(StatusEffect __instance, Character attacker) {
            if (Enabled == null || !Enabled.Value) { return; }
            if (!(__instance is SE_Shield shield)) { return; }

            if (!(attacker is Player)) {
                Casters.Remove(shield);
                return;
            }

            // A player always owns their own character, so the owner is the caster's client. Peer 0
            // would be ZRoutedRpc.Everybody, so an unowned record is never kept.
            ZDO zdo = attacker.m_nview != null ? attacker.m_nview.GetZDO() : null;
            long owner = zdo != null ? zdo.GetOwner() : 0L;
            if (owner == 0L) {
                Casters.Remove(shield);
                return;
            }

            Casters.GetValue(shield, NewCaster).Peer = owner;
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(SE_Shield), nameof(SE_Shield.IsDone))]
        private static void IsDonePrefix(SE_Shield __instance, bool __runOriginal, out Redirect __state) {
            __state = default;
            if (!__runOriginal || Enabled == null || !Enabled.Value) { return; }
            if (__instance.m_levelUpSkillOnBreak == Skills.SkillType.None) { return; }
            if (!(__instance.m_damage > __instance.m_totalAbsorbDamage)) { return; }
            if (!Casters.TryGetValue(__instance, out Caster caster)) { return; }

            // The local player's own shield on themselves: vanilla already credits the right player.
            long local = ZDOMan.instance != null ? ZDOMan.GetSessionID() : 0L;
            Player localPlayer = Player.m_localPlayer;
            if (caster.Peer == local && localPlayer != null && __instance.m_character == localPlayer) { return; }

            __state.Active = true;
            __state.Peer = caster.Peer;
            __state.Skill = __instance.m_levelUpSkillOnBreak;
            __instance.m_levelUpSkillOnBreak = Skills.SkillType.None;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SE_Shield), nameof(SE_Shield.IsDone))]
        private static void IsDonePostfix(SE_Shield __instance, bool __result, Redirect __state) {
            if (!__state.Active || !__result) { return; }

            Casters.Remove(__instance);
            float factor = __instance.m_levelUpSkillFactor;

            if (ZDOMan.instance != null && __state.Peer == ZDOMan.GetSessionID()) {
                RaiseLocal(__state.Skill, factor);
                return;
            }

            ZRoutedRpc.instance?.InvokeRoutedRPC(__state.Peer, RpcName, (int)__state.Skill, factor);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(SE_Shield), nameof(SE_Shield.IsDone))]
        private static void IsDoneFinalizer(SE_Shield __instance, Redirect __state) {
            if (__state.Active) { __instance.m_levelUpSkillOnBreak = __state.Skill; }
        }

        private static void RPC_ShieldBreakSkill(long sender, int skill, float factor) {
            if (Enabled == null || !Enabled.Value) { return; }

            // RaiseSkill on All would create a skill entry with no definition.
            Skills.SkillType type = (Skills.SkillType)skill;
            if (type == Skills.SkillType.None || type == Skills.SkillType.All) { return; }
            if (float.IsNaN(factor) || float.IsInfinity(factor) || factor <= 0f) { return; }

            RaiseLocal(type, factor);
        }

        // Skills.RaiseSkill, as vanilla's break credit, so status effects do not scale it.
        private static void RaiseLocal(Skills.SkillType skill, float factor) {
            Player player = Player.m_localPlayer;
            if (player == null) { return; }

            Skills skills = player.GetSkills();
            if (skills == null) { return; }

            skills.RaiseSkill(skill, factor);
        }
    }
}
