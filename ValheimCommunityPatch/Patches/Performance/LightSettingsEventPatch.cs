using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: every torch, sconce, brazier and campfire subscribes to a STATIC event and
    // unsubscribes from it by linear scan, so lighting a base makes every light's teardown more
    // expensive - and unloading that base pays the whole triangle at once.
    //
    // LightFlicker.cs:78/84:
    //
    //     GraphicsSettingsManager.GraphicsSettingsChanged += new Action(this.ApplySettings);  // OnEnable
    //     GraphicsSettingsManager.GraphicsSettingsChanged -= new Action(this.ApplySettings);  // OnDisable
    //
    // The removal allocates a delegate purely to serve as a search key, then Delegate.Remove ->
    // MulticastDelegate.RemoveImpl -> Array.LastIndexOf walks the entire invocation list comparing
    // with MulticastDelegate.Equals (a target-plus-method compare, not a reference compare). The
    // list is as long as the number of enabled lights, so unloading N lights is O(N^2), and it
    // lands inside Object.Destroy - synchronously, because Unity deactivates a hierarchy inline
    // and defers only the rest. Measured under the unload pass as
    // LightFlicker.OnDisable > remove_GraphicsSettingsChanged > Delegate.Remove >
    // Array.LastIndexOf > MulticastDelegate.Equals. The measured number is small because the
    // session measured was not light-dense; the shape is what matters, and it is quadratic.
    //
    // Fix: a registry keyed on instance id, and an explicit invocation at the one place the event
    // is raised (GraphicsSettingsManager.ApplyGraphicsSettingsToCurrentSession, line 286).
    // Subscribing and unsubscribing become O(1) and allocate nothing.
    //
    // Why only LightFlicker, when four vanilla classes subscribe the same way. CameraEffects and
    // ClutterSystem are singletons; Heightmap is one per loaded zone, so dozens. LightFlicker is
    // one per light, so hundreds - it is what makes the list long, and the list's LENGTH is the
    // defect. Taking the hundreds out of it makes every other subscriber's unsubscribe cheap too,
    // including Heightmap's on the same zone-unload path, without patching them at all.
    //
    // The event itself is left fully functional for any other subscriber - vanilla's three and any
    // mod's - and vanilla's own -= still runs in OnDisable, where it is now a no-op against a list
    // this mod's lights are not in.
    //
    // The toggle is safe to flip at runtime in both directions, because the two mechanisms coexist:
    // a light whose OnEnable ran with the fix off is event-subscribed and served by vanilla's
    // invocation; one whose OnEnable ran with it on is registry-subscribed and served by the
    // postfix below, which is therefore unconditional. Registry removal in OnDisable is
    // unconditional for the same reason.
    //
    // Equivalence: registration happens exactly where vanilla subscribed and under vanilla's own
    // guard (a LightFlicker with no Light returns before subscribing, and still does), removal
    // exactly where vanilla unsubscribed, and the postfix fires at the same statement where the
    // event was raised. ApplySettings is idempotent and reads only settings statics.
    //
    // Client: lights and graphics settings are rendering; nothing headless has either.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(LightFlicker))]
    internal static class LightSettingsEventPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(LightSettingsEventPatch),
                ValConfig.SectionPerformance,
                "Fix Light Settings Subscription",
                true,
                "Registers lights for graphics-setting changes in a lookup table instead of a " +
                "linear event list. Vanilla removes a light from that list by scanning it, so " +
                "the cost of extinguishing or unloading a light grows with how many lights are " +
                "lit - and unloading a torch-heavy base pays that for every one of them at once.");
        }

        // Keyed on GetInstanceID() rather than the LightFlicker: a dictionary keyed on a
        // UnityEngine.Object pays a native CompareBaseObjects call on every probe. See
        // TeardownHooks for the liveness invariant an int key depends on - satisfied here by
        // OnDisable, which Unity raises before OnDestroy and on every deactivation.
        private static readonly Dictionary<int, LightFlicker> Subscribed = new Dictionary<int, LightFlicker>();

        private static bool _hooksChecked;
        private static bool _hooksHealthy;

        // Vanilla's OnEnable verbatim (LightFlicker.cs:76-81) with the event subscribe replaced by
        // a registry add - including its early return for a LightFlicker with no Light, which in
        // vanilla means that instance never subscribes at all.
        [HarmonyPrefix]
        [HarmonyPatch("OnEnable")]
        private static bool OnEnablePrefix(LightFlicker __instance) {
            if (Enabled == null || !Enabled.Value || !HooksHealthy()) { return true; }

            __instance.m_time = 0f;
            if (__instance.m_light == null) { return false; }

            Subscribed[__instance.GetInstanceID()] = __instance;
            __instance.ApplySettings();
            return false;
        }

        // Unconditional: a light registered while the toggle was on must leave the registry even
        // if the toggle is off by the time it is disabled. Vanilla's own -= has already run (this
        // is a postfix) and was a no-op scan for registry-subscribed lights.
        [HarmonyPostfix]
        [HarmonyPatch("OnDisable")]
        private static void OnDisablePostfix(LightFlicker __instance) =>
            Subscribed.Remove(__instance.GetInstanceID());

        [HarmonyPatch(typeof(GraphicsSettingsManager))]
        internal static class SettingsHook {
            // The replacement for vanilla's event invocation, at the same statement in
            // ApplyGraphicsSettingsToCurrentSession where the event fires
            // (GraphicsSettingsManager.cs:286). Unconditional, per the coexistence note above.
            [HarmonyPostfix]
            [HarmonyPatch("ApplyGraphicsSettingsToCurrentSession")]
            private static void Postfix() {
                // ApplySettings only reads settings statics and mutates LightFlicker.Instances,
                // never this registry, so a plain iteration is safe.
                foreach (LightFlicker light in Subscribed.Values) { light.ApplySettings(); }
            }
        }

        // A mod suppressing OnDisable (or a scene teardown race) must not leak the registry across
        // sessions; Shutdown is the session boundary.
        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() => Subscribed.Clear();
        }

        // ---- hook health ---------------------------------------------------------------------

        /// A registry-subscribed light is served ONLY by the hooks below, so OnEnable must not
        /// route lights into the registry unless both attached; otherwise those lights would
        /// silently stop responding to graphics-setting changes.
        private static bool HooksHealthy() {
            if (_hooksChecked) { return _hooksHealthy; }
            _hooksChecked = true;

            _hooksHealthy =
                HasOurPostfix(AccessTools.DeclaredMethod(typeof(LightFlicker), "OnDisable"), typeof(LightSettingsEventPatch))
                && HasOurPostfix(
                    AccessTools.DeclaredMethod(typeof(GraphicsSettingsManager), "ApplyGraphicsSettingsToCurrentSession"),
                    typeof(SettingsHook));

            if (!_hooksHealthy) {
                Logger.LogError(
                    "Light settings subscription: a maintenance hook is not attached, so lights " +
                    "are subscribing through vanilla's event for this session. This usually means " +
                    "a Valheim update changed those methods - look for the patch failure logged " +
                    "at startup.");
            }

            return _hooksHealthy;
        }

        private static bool HasOurPostfix(MethodBase target, System.Type hookClass) {
            // Fully qualified: HarmonyLib.Patches collides with this mod's own Patches namespace.
            HarmonyLib.Patches info = target == null ? null : Harmony.GetPatchInfo(target);
            if (info == null) { return false; }

            foreach (Patch patch in info.Postfixes) {
                if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                if (patch.PatchMethod == null || patch.PatchMethod.DeclaringType != hookClass) { continue; }
                return true;
            }

            return false;
        }
    }
}
