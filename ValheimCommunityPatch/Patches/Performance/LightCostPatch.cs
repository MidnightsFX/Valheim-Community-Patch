using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Two related costs at high torch density, measured in a large base:
    //
    //  - LightFlicker.CustomUpdate runs for every flickering light every frame and is mostly
    //    native interop: an alive-check on the Light, get_enabled, set_intensity, and a
    //    localPosition write, per instance per frame (~5.5 s of a 10-minute window at a few
    //    hundred torches). Flicker is invisible past a few dozen metres - and when the game's
    //    light LOD is active the light itself is faded out beyond 40 m - so all of that work on
    //    distant lights buys nothing.
    //
    //  - The game ships a complete priority-ranked point-light cap it never wires up: LightLod
    //    fades lights beyond 40 m, and when LightLod.m_lightLimit >= 0 it also keeps only the
    //    nearest N point lights enabled, re-ranked once a second (LightLod.UpdateLights,
    //    LightLod.cs:121-144, which early-outs entirely while both limits are negative). The
    //    graphics settings drive only the shadow half (m_shadowLimit,
    //    GraphicsSettingsManager.cs:541); nothing in the game or its UI ever sets m_lightLimit.
    //
    // Fix, part one (the toggle): a distance LOD for the flicker updates. Each instance's world
    // position is cached and refreshed with one real transform read every few frames - so carried
    // torches stay correct - and between refreshes the distance gate is pure math against a
    // once-per-frame player position. Beyond the configured distance the update is skipped
    // entirely: zero interop for far lights. Instances with a TTL always update, because their
    // self-destruction runs inside CustomUpdate (LightFlicker.cs:105-111) and must not stall.
    //
    // Fix, part two (an exposed vanilla setting, not a behaviour change): a client-local
    // "Point Light Limit" that simply assigns LightLod.m_lightLimit. Default -1 is exactly
    // vanilla; a value like 30 turns a 200-torch hall into the nearest 30 real lights using the
    // game's own smooth fade-in/out, with the ranking cost piggybacking on machinery the shadow
    // limit already pays for. Client-local on purpose - it is a per-machine visual/performance
    // preference, not a property of the world - so it deliberately bypasses the server-synced
    // config helpers.
    //
    // Client: lights and flicker are rendering; nothing headless has either.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(LightFlicker))]
    internal static class LightCostPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> FlickerDistance;
        internal static ConfigEntry<int> PointLightLimit;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(LightCostPatch),
                ValConfig.SectionPerformance,
                "Fix Light Flicker Overhead",
                true,
                "Stops updating torch flicker for lights too far away for the flicker to be " +
                "visible. At high torch density the per-light engine calls are a steady share of " +
                "frame time.");

            // Client-local visual preferences, deliberately not server-synced.
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

        // The whole of part two: the game reads this static every ranking tick and every
        // per-light LOD cycle; nothing else ever writes it.
        private static void ApplyLightLimit() => LightLod.m_lightLimit = PointLightLimit.Value;

        private struct Anchor {
            public int m_frame;
            // The gate decision is cached with the anchor: between refreshes the prefix is one
            // dictionary hit and a bool. A skip flips at most 10 frames late - centimetre-level
            // hysteresis on a 45 m gate.
            public bool m_skip;
        }

        // How many frames a cached light position may age before one real transform read. Carried
        // torches move at player speed, so at 10 frames the anchor is at most a couple of metres
        // stale - noise against a 45 m gate.
        private const int AnchorRefreshFrames = 10;

        private static readonly Dictionary<LightFlicker, Anchor> Anchors = new Dictionary<LightFlicker, Anchor>();

        private static int _playerPosFrame = -1;
        private static Vector3 _playerPos;

        [HarmonyPrefix]
        [HarmonyPatch(nameof(LightFlicker.CustomUpdate))]
        private static bool CustomUpdatePrefix(LightFlicker __instance) {
            if (Enabled == null || !Enabled.Value) { return true; }

            // TTL instances destroy themselves from inside CustomUpdate; never starve them.
            if (__instance.m_ttl > 0f) { return true; }

            Player player = Player.m_localPlayer;
            if (ReferenceEquals(player, null)) { return true; }

            int frame = Time.frameCount;
            if (frame != _playerPosFrame) {
                _playerPosFrame = frame;
                _playerPos = player.transform.position;
            }

            if (!Anchors.TryGetValue(__instance, out Anchor anchor) || frame - anchor.m_frame >= AnchorRefreshFrames) {
                Vector3 pos = __instance.transform.position;
                float dx = pos.x - _playerPos.x;
                float dz = pos.z - _playerPos.z;
                float limit = FlickerDistance != null ? FlickerDistance.Value : 45f;

                anchor = new Anchor { m_frame = frame, m_skip = dx * dx + dz * dz > limit * limit };
                Anchors[__instance] = anchor;
            }

            return !anchor.m_skip;
        }

        // Vanilla unregisters the instance here (LightFlicker.cs:82-86); the anchor goes with it.
        [HarmonyPatch(typeof(LightFlicker), "OnDisable")]
        internal static class DisableHook {
            [HarmonyPostfix]
            private static void Postfix(LightFlicker __instance) => Anchors.Remove(__instance);
        }
    }
}
