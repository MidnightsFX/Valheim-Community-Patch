using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Inventory buttons play their ButtonSfx click plus SetActiveGroup's identical one, so a press sounds twice.
    // Prefixes and postfixes on SetActiveGroup and ButtonSfx.PlaySfx stamp the frame each played in and skip the other.
    // Each press plays one click and one vibration.
    // Provenance: Krosshair's Valheim Wire Patches, ported and reworked.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class DoubleClickSoundPatch {
        internal static FixToggle Enabled;

        private static int _groupFrame = -1;
        private static int _clickFrame = -1;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(DoubleClickSoundPatch),
                ValConfig.SectionCorrectness,
                "Fix Double Click Sound",
                true,
                "Inventory tabs and buttons such as Craft, Upgrade, Skills and Trophies play their click " +
                "sound once instead of twice.");
        }

        // The second overload takes a group; this is the one every call ends in. The prefix only
        // suppresses; the frame is stamped in the postfix, once the effect really exists. The group
        // effect cannot simply be dropped: it is the only click the Craft and Repair buttons and the
        // recipe rows have. Whichever source runs first plays, the same sound either way, so the
        // order a button's listeners were added in does not matter.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(InventoryGui), "SetActiveGroup", new[] { typeof(int), typeof(bool) })]
        private static void SetActiveGroupPrefix(ref bool playSound, bool __runOriginal) {
            if (!__runOriginal || Enabled == null || !Enabled.Value || !playSound) { return; }
            if (_clickFrame == Time.frameCount) { playSound = false; }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryGui), "SetActiveGroup", new[] { typeof(int), typeof(bool) })]
        private static void SetActiveGroupPostfix(InventoryGui __instance, bool playSound, bool __runOriginal) {
            if (!__runOriginal || Enabled == null || !Enabled.Value || !playSound) { return; }
            // Vanilla's own gate on the effect, plus that the list really created something.
            if (Player.m_localPlayer == null || !WillPlay(__instance.m_setActiveGroupEffects)) { return; }
            _groupFrame = Time.frameCount;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(ButtonSfx), "PlaySfx")]
        private static void PlaySfxPrefix(ButtonSfx __instance, ref GameObject soundPrefab, ref GameObject vibrationPrefab, bool __runOriginal) {
            if (!__runOriginal || Enabled == null || !Enabled.Value) { return; }
            // The click path only: select and hover sounds come through here with other prefabs.
            if (soundPrefab == null || !ReferenceEquals(soundPrefab, __instance.m_sfxPrefab)) { return; }
            if (_groupFrame == Time.frameCount) {
                soundPrefab = null;
                vibrationPrefab = null;
            }
        }

        // soundPrefab is null here when the prefix dropped it. The timer's last frame says whether
        // vanilla really played the click, which its shared two-frame throttle can refuse. Vanilla
        // throttles vibration separately, so a throttled click can still vibrate twice, as in vanilla.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ButtonSfx), "PlaySfx")]
        private static void PlaySfxPostfix(ButtonSfx __instance, GameObject soundPrefab, bool __runOriginal) {
            if (!__runOriginal || Enabled == null || !Enabled.Value) { return; }
            if (soundPrefab == null || !ReferenceEquals(soundPrefab, __instance.m_sfxPrefab)) { return; }
            int frame = Time.frameCount;
            if (ButtonSfx.m_sfxTimer.m_lastTriggerFrame == frame) { _clickFrame = frame; }
        }

        // What EffectList.Create instantiates: an enabled entry with a prefab. HasEffects alone would
        // also count a disabled or missing one and stamp a sound that never plays.
        private static bool WillPlay(EffectList effects) {
            EffectList.EffectData[] entries = effects?.m_effectPrefabs;
            if (entries == null) { return false; }
            for (int i = 0; i < entries.Length; i++) {
                EffectList.EffectData entry = entries[i];
                if (entry != null && entry.m_enabled && entry.m_prefab != null) { return true; }
            }
            return false;
        }
    }
}
