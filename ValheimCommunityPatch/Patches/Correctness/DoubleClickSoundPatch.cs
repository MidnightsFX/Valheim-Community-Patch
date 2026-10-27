using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Double Click Sound: inventory tabs and buttons play their click sound and vibration once.
    //
    // InventoryGui.SetActiveGroup creates m_setActiveGroupEffects, the gui button click and its
    // vibration, whenever it is called with sound: that is how the gamepad's bumpers get a click
    // when they switch panels. The Craft and Upgrade tabs, the Skills, Trophies and Texts buttons
    // and the PVP toggle carry a ButtonSfx whose click is the very same two prefabs, and their
    // handlers call SetActiveGroup too, so one press plays the sound and the vibration twice. The
    // Craft and Repair buttons and the recipe rows have no click of their own, so the group effect
    // is their only click and cannot simply be dropped.
    //
    // A postfix on each source stamps the frame it really played in, and a prefix on each skips it
    // when the other source's stamp is this frame: SetActiveGroup passes playSound as false when a
    // ButtonSfx click already played, and ButtonSfx.PlaySfx drops its click sound and vibration when
    // the group effect already played. Whichever runs first plays, which is the same sound either
    // way, so the order the button's listeners were added in does not matter. Only the click prefab
    // is matched, never a select or hover sound, and a ButtonSfx click that its own two-frame
    // throttle refused stamps nothing. Vanilla throttles vibration separately from sound, so a
    // throttled click can still vibrate twice, as it does in vanilla.
    //
    // Client: the inventory screen. Provenance: ported from Krosshair's Valheim Wire Patches and
    // reworked.
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
                "sound once instead of twice. Vanilla plays the panel-switch click meant for the gamepad " +
                "on top of the button's own identical click.");
        }

        // The second overload takes a group; this is the one every call ends in. The prefix only
        // suppresses; the frame is stamped in the postfix, once the effect really exists.
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
        // vanilla really played the click, which its shared two-frame throttle can refuse.
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
