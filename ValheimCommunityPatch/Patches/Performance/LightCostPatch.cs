using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Light Flicker Overhead: torch flicker stops updating for lights beyond both a configurable
    // distance and the light's own LOD distance.
    //
    // LightFlicker.CustomUpdate runs for every flickering light every frame and is mostly native
    // calls: an alive-check, get_enabled, set_intensity and a localPosition write. It is also what
    // gives a light its brightness, because ApplySettings, which runs on enable and on every
    // graphics-settings change, sets the intensity to zero. LightLod keeps each light switched on
    // out to its own m_lightDistance (40 m by default, 80 m for torches, 100 m for fires).
    //
    // A prefix caches each light's LOD distance and whether it is out of range, re-measured every
    // ten frames so carried torches stay correct, and skips the update when the light is beyond
    // both Light Flicker Distance and its LOD distance from LightLod's own reference point (the
    // player, or the camera in free fly). A light is not skipped until one update has run with it
    // switched on since its last ApplySettings, so a skipped light holds its last lit intensity
    // instead of zero. Lights with a TTL always update because they destroy themselves from inside
    // CustomUpdate. The distance is client-local because it is a per-machine preference.
    //
    // Client: lights and flicker are rendering.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(LightFlicker))]
    internal static class LightCostPatch {
        internal static ConfigEntry<float> FlickerDistance;

        internal static void BindConfig() {
            FlickerDistance = ValConfig.cfg.Bind(
                "Client config",
                "Light Flicker Distance",
                45f,
                new ConfigDescription(
                    "Metres beyond which torch flicker stops updating. A light always keeps updating " +
                    "inside its own light LOD distance, where the game keeps it switched on (40 m for " +
                    "most lights, 80 m for torches, 100 m for fires), so this only affects lights past both.",
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

        [HarmonyPrefix]
        [HarmonyPatch(nameof(LightFlicker.CustomUpdate))]
        private static bool CustomUpdatePrefix(LightFlicker __instance) {
            if (__instance.m_ttl > 0f) { return true; }

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

            // Vanilla's update restores the intensity, but only while the light is switched on.
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
