using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Light Flicker Overhead: LightFlicker.CustomUpdate runs every frame for every flickering light, however far.
    // Prefixes CustomUpdate to skip lights past both 'Light Flicker Distance' and their own LOD distance.
    // Distant flickering lights stop costing a per-frame update and hold their last lit brightness.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(LightFlicker))]
    internal static class LightCostPatch {
        internal static ConfigEntry<float> FlickerDistance;

        // Client-local, not server-synced: a per-machine preference.
        internal static void BindConfig() {
            FlickerDistance = ValConfig.cfg.Bind(
                "Client config",
                "Light Flicker Distance",
                45f,
                new ConfigDescription(
                    "Distance in meters beyond which torch flicker stops updating. Lights always update inside " +
                    "the distance where the game switches them off (40 m, torches 80 m, fires 100 m).",
                    new AcceptableValueRange<float>(10f, 200f),
                    new ConfigurationManagerAttributes { IsAdvanced = true }));
        }

        private struct Anchor {
            public int m_frame;
            public float m_lodDistance;
            // Cached with the anchor so the prefix is one dictionary hit and a bool between
            // refreshes. A skip flips at most ten frames late, which is centimetres on these gates.
            public bool m_skip;
            // An update has run with the light switched on since ApplySettings last zeroed it.
            public bool m_lit;
        }

        private const int AnchorRefreshFrames = 10;

        // Keyed on GetInstanceID() for consistency with the other registries (see TeardownHooks).
        // On this single-probe path the id lookup costs about as much as the object key it
        // replaced, so the real saving here is the skipped update, not the key.
        private static readonly Dictionary<int, Anchor> Anchors = new Dictionary<int, Anchor>();

        private static int _referenceFrame = -1;
        private static Vector3 _reference;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(LightCostPatch));

        [HarmonyPrefix]
        [HarmonyPatch(nameof(LightFlicker.CustomUpdate))]
        private static bool CustomUpdatePrefix(LightFlicker __instance) {
            // A light with a TTL destroys itself from inside CustomUpdate, so it always runs.
            if (ApiSwitch.Off || __instance.m_ttl > 0f) { return true; }

            Player player = Player.m_localPlayer;
            if (ReferenceEquals(player, null)) { return true; }

            int frame = Time.frameCount;
            if (frame != _referenceFrame) {
                _referenceFrame = frame;
                Camera camera = GameCamera.InFreeFly() ? Utils.GetMainCamera() : null;
                _reference = camera != null ? camera.transform.position : player.transform.position;
            }

            int id = __instance.GetInstanceID();
            bool known = Anchors.TryGetValue(id, out Anchor anchor);
            if (!known) {
                LightLod lod = __instance.GetComponent<LightLod>();
                anchor.m_lodDistance = lod != null && lod.m_lightLod ? lod.m_lightDistance : 0f;
            }

            if (!known || frame - anchor.m_frame >= AnchorRefreshFrames) {
                float limit = Mathf.Max(FlickerDistance != null ? FlickerDistance.Value : 45f, anchor.m_lodDistance);
                anchor.m_frame = frame;
                anchor.m_skip = (__instance.transform.position - _reference).sqrMagnitude > limit * limit;
                Anchors[id] = anchor;
            }

            if (!anchor.m_skip || anchor.m_lit) { return !anchor.m_skip; }

            // ApplySettings (on enable and on every graphics-settings change) zeroes the intensity and
            // only vanilla's update restores it, while the light is switched on; so a light is skipped
            // only after one such update, and holds its last lit intensity instead of zero.
            Light light = __instance.m_light;
            if (light != null && light.enabled) {
                anchor.m_lit = true;
                Anchors[id] = anchor;
            }

            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch("ApplySettings")]
        private static void ApplySettingsPostfix(LightFlicker __instance) {
            int id = __instance.GetInstanceID();
            if (Anchors.TryGetValue(id, out Anchor anchor) && anchor.m_lit) {
                anchor.m_lit = false;
                Anchors[id] = anchor;
            }
        }

        // Vanilla unregisters the instance here; the anchor goes with it.
        [HarmonyPatch(typeof(LightFlicker), "OnDisable")]
        internal static class DisableHook {
            [HarmonyPostfix]
            private static void Postfix(LightFlicker __instance) => Anchors.Remove(__instance.GetInstanceID());
        }
    }
}
