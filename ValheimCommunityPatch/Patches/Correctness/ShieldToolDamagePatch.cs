using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Shield Tool Damage: stops a shield, such as the Staff of Protection's or a Fuling shaman's,
    // spending its strength on chop and pickaxe damage, which is there to break trees, rocks and
    // buildings and cannot hurt the player or creature wearing it.
    //
    // SE_Shield.OnDamaged adds hit.GetTotalDamage() to the damage it has absorbed and zeroes the hit.
    // That total includes m_chop and m_pickaxe, and the shield sees the hit before the wearer's own
    // resistances, which make players and every vanilla creature immune to both. Many creature attacks
    // carry them so they can break walls, often more than their real damage: an Abomination swing is
    // 60 blunt and 160 chop and pickaxe, a Bonemass punch 130 real damage and 2000 chop and pickaxe,
    // so one punch breaks even a Blood Magic 100 shield. Axes, battleaxes and pickaxes carry them
    // too, so they broke a Fuling shaman's shield faster than their real damage could.
    //
    // A prefix lifts chop and pickaxe out of the hit for the one call, each only when the wearer is
    // immune to it, so vanilla counts and absorbs only the rest; a finalizer puts them back for the
    // wearer's resistances to settle as they would without a shield. The prefix runs after other
    // mods' prefixes and leaves the hit alone when one of them already replaced the call.
    //
    // Both: OnDamaged runs on whichever side owns the wearer, a dedicated server for a creature it
    // simulates.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(SE_Shield), nameof(SE_Shield.OnDamaged))]
    internal static class ShieldToolDamagePatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ShieldToolDamagePatch),
                ValConfig.SectionCorrectness,
                "Fix Shield Tool Damage",
                true,
                "Stops a shield, such as the Staff of Protection's or a Fuling shaman's, using up its " +
                "strength on chop and pickaxe damage, which breaks trees, rocks and buildings but cannot " +
                "hurt players or creatures. Vanilla counted it as real damage, so a single Bonemass punch " +
                "broke even a fully skilled Staff of Protection shield, and axes and pickaxes broke a " +
                "Fuling shaman's shield faster than their real damage could.");
        }

        private struct Lifted {
            internal bool Chop;
            internal bool Pickaxe;
            internal float ChopDamage;
            internal float PickaxeDamage;
        }

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(SE_Shield __instance, HitData hit, bool __runOriginal, out Lifted __state) {
            __state = default;
            if (!__runOriginal || Enabled == null || !Enabled.Value || hit == null) { return; }
            if (hit.m_damage.m_chop <= 0f && hit.m_damage.m_pickaxe <= 0f) { return; }

            Character wearer = __instance.m_character;
            if (wearer == null) { return; }

            // The same modifiers RPC_Damage applies after the shield, weak spot included.
            HitData.DamageModifiers mods = wearer.GetDamageModifiers(wearer.GetWeakSpot(hit.m_weakSpot));

            // Damage the wearer can take stays in the hit, for the shield to absorb as vanilla does.
            if (CannotHurt(mods.m_chop)) {
                __state.Chop = true;
                __state.ChopDamage = hit.m_damage.m_chop;
                hit.m_damage.m_chop = 0f;
            }

            if (CannotHurt(mods.m_pickaxe)) {
                __state.Pickaxe = true;
                __state.PickaxeDamage = hit.m_damage.m_pickaxe;
                hit.m_damage.m_pickaxe = 0f;
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer(HitData hit, Lifted __state) {
            if (__state.Chop) { hit.m_damage.m_chop = __state.ChopDamage; }
            if (__state.Pickaxe) { hit.m_damage.m_pickaxe = __state.PickaxeDamage; }
        }

        private static bool CannotHurt(HitData.DamageModifier modifier) =>
            modifier == HitData.DamageModifier.Immune || modifier == HitData.DamageModifier.Ignore;
    }
}
