using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Light Settings Subscription: lights register for graphics-setting changes in a lookup
    // table instead of a static event whose unsubscribe scans every other subscriber.
    //
    // LightFlicker.OnEnable subscribes ApplySettings to the static
    // GraphicsSettingsManager.GraphicsSettingsChanged event and OnDisable unsubscribes. Removing
    // from a multicast delegate allocates a delegate as the search key and walks the whole
    // invocation list comparing with MulticastDelegate.Equals, and the list is as long as the
    // number of lit lights, so unloading N lights is O(N^2), synchronously inside Object.Destroy.
    //
    // OnEnable is replaced with a copy that registers the light in a dictionary keyed on instance
    // id instead of subscribing; an OnDisable postfix removes it; and a postfix on the one method
    // that raises the event calls ApplySettings on every registered light. Only LightFlicker is
    // patched: the other three vanilla subscribers are singletons or one-per-zone, and taking the
    // hundreds of lights out of the list makes their unsubscribes cheap too. The event stays fully
    // functional for everyone else. A light whose OnEnable ran before the hooks were healthy is
    // event-subscribed and served by vanilla, so the postfix and the removal are unconditional.
    //
    // Client: lights and graphics settings are rendering.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(LightFlicker))]
    internal static class LightSettingsEventPatch {
        // Keyed on GetInstanceID(); see TeardownHooks for the int-key rationale and invariant.
        // OnDisable runs before OnDestroy and on every deactivation, which satisfies it.
        private static readonly Dictionary<int, LightFlicker> Subscribed = new Dictionary<int, LightFlicker>();

        // A registered light is served only by these hooks, so OnEnable must not route lights
        // into the registry unless both attached.
        private static readonly HookHealth Hooks = new HookHealth(
            "Light settings subscription",
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(LightFlicker), "OnDisable"), typeof(LightSettingsEventPatch))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(GraphicsSettingsManager), "ApplyGraphicsSettingsToCurrentSession"), typeof(SettingsHook)));

        // Vanilla's OnEnable with the event subscribe replaced by a registry add, including its
        // early return for a LightFlicker with no Light.
        [HarmonyPrefix]
        [HarmonyPatch("OnEnable")]
        private static bool OnEnablePrefix(LightFlicker __instance) {
            if (!Hooks.Healthy) { return true; }

            __instance.m_time = 0f;
            if (__instance.m_light == null) { return false; }

            Subscribed[__instance.GetInstanceID()] = __instance;
            __instance.ApplySettings();
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch("OnDisable")]
        private static void OnDisablePostfix(LightFlicker __instance) =>
            Subscribed.Remove(__instance.GetInstanceID());

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
