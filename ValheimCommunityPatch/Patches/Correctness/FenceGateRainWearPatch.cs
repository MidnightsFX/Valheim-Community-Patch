using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // The Deep North wood fence gate kept m_noRoofWear on, so it wears to half health in rain while its fence does not.
    // A ZNetScene.Awake postfix turns m_noRoofWear off on the gate prefab, and on loaded gates when the toggle changes.
    // Wood fence gates no longer take rain or underwater damage, like the fence they are built into.
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
                "Stops the wood fence gate taking damage in the rain, as its fence does not; every player " +
                "should have the mod, since the game that owns the gate works out rain damage.");

            Enabled.SettingChanged += (sender, args) => Apply();
        }

        private static readonly int GateHash = "wood_fence_gate".GetStableHashCode();

        // The gate prefab's WearNTear and the m_noRoofWear it had before the fix changed it. Every gate,
        // loaded, received or placed with the hammer, is created from this prefab. It outlives the
        // world, so a turn-off from the main menu still reaches it; Unity null once the prefab is
        // unloaded, and the next world's Awake then finds the reloaded one.
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
