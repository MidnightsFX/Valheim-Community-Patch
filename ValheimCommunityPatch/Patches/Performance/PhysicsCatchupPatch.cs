using BepInEx.Configuration;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Physics Catchup Spiral: caps how many fixed physics steps one frame may run to catch up
    // after a stall.
    //
    // Unity's Time.maximumDeltaTime ships at 0.333 s, so a single long frame is followed by up to
    // ~16 fixed physics steps of catch-up in the next frame, which makes that frame long too.
    // Every hitch becomes two.
    //
    // Time.maximumDeltaTime is set to N * fixedDeltaTime (default 8) at bind time and on every
    // config change. Time the simulation cannot cover is dropped, exactly as vanilla drops it past
    // its own higher cap. Not a Harmony patch: the engine never rewrites this global mid-session.
    //
    // Both. Provenance: ontrigger's ValheimPerformanceOptimizations (MIT), same default.
    [PatchSide(Side.Both)]
    internal static class PhysicsCatchupPatch {
        internal static ConfigEntry<int> MaxSteps;

        internal static void BindConfig() {
            MaxSteps = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Max Physics Steps Per Frame",
                8,
                "How many fixed physics steps a single frame may run while catching up after a " +
                "stall. Lower recovers from hitches faster but drops more simulated time " +
                "during them; vanilla's effective value is ~16.",
                advanced: true,
                valMin: 4,
                valMax: 15);

            Apply();
            MaxSteps.SettingChanged += (sender, args) => Apply();
        }

        private static void Apply() {
            if (MaxSteps == null) { return; }

            Time.maximumDeltaTime = MaxSteps.Value * Time.fixedDeltaTime;
        }
    }
}
