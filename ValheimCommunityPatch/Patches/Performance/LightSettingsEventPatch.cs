using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Light Settings Subscription: each LightFlicker leaving a static settings event scans every other subscriber.
    // Prefixes LightFlicker.OnEnable to register in a dictionary that a GraphicsSettingsManager postfix serves instead.
    // Unloading many lights no longer stalls on event removal, whose cost grew with the square of the light count.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(LightFlicker))]
    internal static class LightSettingsEventPatch {
        // Keyed on GetInstanceID(); see TeardownHooks for the int-key rationale and invariant.
        // OnDisable runs before OnDestroy and on every deactivation, which satisfies it.
        private static readonly Dictionary<int, LightFlicker> Subscribed = new Dictionary<int, LightFlicker>();

        private static readonly FixSwitch ApiSwitch = CreateSwitch();

        // A registered light is served only by these hooks, so OnEnable must not route lights
        // into the registry unless both attached.
        private static readonly HookHealth Hooks = new HookHealth(
            typeof(LightSettingsEventPatch),
            "Light settings subscription",
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(LightFlicker), "OnDisable"), typeof(LightSettingsEventPatch))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(GraphicsSettingsManager), "ApplyGraphicsSettingsToCurrentSession"), typeof(SettingsHook)));

        // Vanilla's OnEnable with the event subscribe replaced by a registry add, including its
        // early return for a LightFlicker with no Light. Only LightFlicker: the other vanilla
        // subscribers are few, and their unsubscribes get cheap once the lights are off the list.
        [HarmonyPrefix]
        [HarmonyPatch("OnEnable")]
        private static bool OnEnablePrefix(LightFlicker __instance) {
            if (ApiSwitch.Off || !Hooks.Healthy) { return true; }

            __instance.m_time = 0f;
            if (__instance.m_light == null) { return false; }

            Subscribed[__instance.GetInstanceID()] = __instance;
            __instance.ApplySettings();
            return false;
        }

        // Unconditional, like the settings postfix: a light that subscribed to the event instead is
        // not in the registry, and vanilla serves it.
        [HarmonyPostfix]
        [HarmonyPatch("OnDisable")]
        private static void OnDisablePostfix(LightFlicker __instance) =>
            Subscribed.Remove(__instance.GetInstanceID());

        // Turned off, the registered lights move onto the game's event, as their OnEnable would
        // have put them, so the game's own OnDisable takes them off again.
        private static FixSwitch CreateSwitch() {
            FixSwitch fixSwitch = FixRegistry.SwitchOf(typeof(LightSettingsEventPatch));
            fixSwitch.TurnedOff += () => {
                foreach (LightFlicker light in Subscribed.Values) {
                    if (light != null) { GraphicsSettingsManager.GraphicsSettingsChanged += light.ApplySettings; }
                }

                Subscribed.Clear();
            };
            return fixSwitch;
        }

        [HarmonyPatch(typeof(GraphicsSettingsManager))]
        internal static class SettingsHook {
            // The same statement in ApplyGraphicsSettingsToCurrentSession where the event fires.
            [HarmonyPostfix]
            [HarmonyPatch("ApplyGraphicsSettingsToCurrentSession")]
            private static void Postfix() {
                foreach (LightFlicker light in Subscribed.Values) { light.ApplySettings(); }
            }
        }

        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() => Subscribed.Clear();
        }
    }
}
