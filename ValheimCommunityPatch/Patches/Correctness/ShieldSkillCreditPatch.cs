using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // SE_Shield drops its caster, so a broken shield's Blood Magic goes to the wearer, or nobody on a tamed creature.
    // A SetAttacker postfix records the caster; IsDone hooks move the break credit to them, by routed RPC when remote.
    // The player who cast a Staff of Protection shield gets its Blood Magic when it breaks.
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
                "Gives the Blood Magic a broken Staff of Protection shield earns to the player who cast it " +
                "instead of whoever wore it; the caster and the shielded player both need the mod.");
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

        // Clears the skill for this one call, so vanilla still plays the break effects but raises nothing; the
        // finalizer puts it back. Priority.Last: see ValheimCommunityPatch.ApplyPatches.
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

            // A caster without the mod has no handler, so the credit is lost.
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
