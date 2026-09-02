using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Light Flicker Overhead: torch flicker stops updating beyond a configurable distance,
    // and the game's dormant point-light cap is exposed as a setting.
    //
    // LightFlicker.CustomUpdate runs for every flickering light every frame and is mostly native
    // calls: an alive-check, get_enabled, set_intensity and a localPosition write. Flicker is
    // invisible past a few dozen metres, and the game's own light LOD fades the light out at
    // 40 m. Separately, LightLod has a complete nearest-N point-light cap (m_lightLimit) that
    // nothing in the game or its settings UI ever sets.
    //
    // A prefix caches each light's position, refreshed with one transform read every ten frames
    // so carried torches stay correct, and skips the update when the light is beyond Light
    // Flicker Distance from the player. Lights with a TTL always update because they destroy
    // themselves from inside CustomUpdate. Point Light Limit assigns LightLod.m_lightLimit; -1
    // is exactly vanilla. Both entries are client-local because they are per-machine preferences.
    //
    // Client: lights and flicker are rendering.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(LightFlicker))]
    internal static class LightCostPatch {
        internal static ConfigEntry<float> FlickerDistance;
        internal static ConfigEntry<int> PointLightLimit;

        internal static void BindConfig() {
            FlickerDistance = ValConfig.cfg.Bind(
                "Client config",
                "Light Flicker Distance",
                45f,
                new ConfigDescription(
                    "Metres beyond which torch flicker stops updating. The game's own light LOD " +
                    "fades the light itself out at 40, so flicker past that is invisible anyway.",
                    new AcceptableValueRange<float>(10f, 200f),
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            PointLightLimit = ValConfig.cfg.Bind(
                "Client config",
                "Point Light Limit",
                -1,
                new ConfigDescription(
                    "Caps how many of the nearest point lights are enabled at once, using the " +
                    "game's own dormant light-priority system with its smooth fade. -1 (default) " +
                    "is exactly vanilla: no cap. Try 30-50 in torch-heavy bases.",
                    new AcceptableValueRange<int>(-1, 200),
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            ApplyLightLimit();
            PointLightLimit.SettingChanged += (sender, args) => ApplyLightLimit();
        }

        private static void ApplyLightLimit() => LightLod.m_lightLimit = PointLightLimit.Value;

        private struct Anchor {
            public int m_frame;
            // Cached with the anchor so the prefix is one dictionary hit and a bool between
            // refreshes. A skip flips at most ten frames late, which is centimetres on a 45 m gate.
            public bool m_skip;
        }

        private const int AnchorRefreshFrames = 10;

        // Keyed on GetInstanceID() for consistency with the other registries (see TeardownHooks).
        // On this single-probe path the id lookup costs about as much as the object key it
        // replaced, so the real saving here is the skipped update, not the key.
        private static readonly Dictionary<int, Anchor> Anchors = new Dictionary<int, Anchor>();

        private static int _playerPosFrame = -1;
        private static Vector3 _playerPos;

        [HarmonyPrefix]
        [HarmonyPatch(nameof(LightFlicker.CustomUpdate))]
        private static bool CustomUpdatePrefix(LightFlicker __instance) {
            if (__instance.m_ttl > 0f) { return true; }

            Player player = Player.m_localPlayer;
            if (ReferenceEquals(player, null)) { return true; }

            int frame = Time.frameCount;
            if (frame != _playerPosFrame) {
                _playerPosFrame = frame;
                _playerPos = player.transform.position;
            }

            int id = __instance.GetInstanceID();
            if (!Anchors.TryGetValue(id, out Anchor anchor) || frame - anchor.m_frame >= AnchorRefreshFrames) {
                Vector3 pos = __instance.transform.position;
                float dx = pos.x - _playerPos.x;
                float dz = pos.z - _playerPos.z;
                float limit = FlickerDistance != null ? FlickerDistance.Value : 45f;

                anchor = new Anchor { m_frame = frame, m_skip = dx * dx + dz * dz > limit * limit };
                Anchors[id] = anchor;
            }

            return !anchor.m_skip;
        }

        // Vanilla unregisters the instance here; the anchor goes with it.
        [HarmonyPatch(typeof(LightFlicker), "OnDisable")]
        internal static class DisableHook {
            [HarmonyPostfix]
            private static void Postfix(LightFlicker __instance) => Anchors.Remove(__instance.GetInstanceID());
        }
    }
}
