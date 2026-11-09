using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Fence Gate Rain Damage: stops the wood fence gate wearing down in the rain, as the wood
    // fence it is built into does not.
    //
    // WearNTear wears a piece left in the open down to half health while it rains, or while it sits
    // under water, when its m_noRoofWear is on, which is the field's default. The wood fence and the
    // stave gate have it turned off, but the wood fence gate added in the Deep North update kept the
    // default, so a gate in a fence loses health every rainy minute while the fence beside it stays
    // whole. Its other wear settings already match the fence's.
    //
    // A ZNetScene.Awake postfix turns m_noRoofWear off on the gate's prefab as each world loads, and
    // keeps the value it found there. Every gate is created from that prefab, whether loaded from the
    // world, sent by another player or placed with the hammer, whose piece table holds the same
    // object, so each one starts without rain wear. When the fix is turned on or off mid-session the
    // prefab gets its new value and every loaded gate still holding the old value is changed to match;
    // turning it off puts back the value the prefab had. The prefab object outlives the world, so it
    // is kept between worlds and a turn-off from the main menu still reaches it.
    //
    // Both: rain wear is run by the game that owns the gate, a player's client or a listen host.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class FenceGateRainWearPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(FenceGateRainWearPatch),
                ValConfig.SectionCorrectness,
                "Fix Fence Gate Rain Damage",
                true,
                "Stops the wood fence gate taking damage in the rain, as the wood fence it is built into " +
                "does not. Rain damage is worked out by the game that owns the gate, so every player " +
                "should have the mod.");

            Enabled.SettingChanged += (sender, args) => Apply();
        }

        private static readonly int GateHash = "wood_fence_gate".GetStableHashCode();

        // The gate prefab's WearNTear and the m_noRoofWear it had before the fix changed it. Unity
        // null once the prefab is unloaded; the next world's Awake then finds the reloaded one.
        private static WearNTear _prefab;
        private static bool _prefabValue;

        // Priority.Last: a value another mod sets on the prefab in its own postfix is the one kept.
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("Awake")]
        private static void AwakePostfix(ZNetScene __instance) {
            GameObject gate = __instance.GetPrefab(GateHash);
            WearNTear wear = gate != null ? gate.GetComponent<WearNTear>() : null;
            if (wear == null) { return; }

            // The same prefab as last world still holds the fix's value, not its own.
            if (wear != _prefab) {
                _prefab = wear;
                _prefabValue = wear.m_noRoofWear;
            }

            Apply();
        }

        private static void Apply() {
            if (_prefab == null || Enabled == null) { return; }

            bool before = _prefab.m_noRoofWear;
            bool after = !Enabled.Value && _prefabValue;
            if (before == after) { return; }

            _prefab.m_noRoofWear = after;

            // Gates already in the world copied the prefab when they were created. One holding another
            // value was set by something else and is left alone.
            foreach (WearNTear piece in WearNTear.GetAllInstances()) {
                if (piece == null || piece.m_noRoofWear != before) { continue; }

                ZDO zdo = piece.m_nview != null ? piece.m_nview.GetZDO() : null;
                if (zdo != null && zdo.GetPrefab() == GateHash) { piece.m_noRoofWear = after; }
            }
        }
    }
}
