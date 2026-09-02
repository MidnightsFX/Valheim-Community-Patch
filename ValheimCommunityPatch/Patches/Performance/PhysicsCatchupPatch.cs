using BepInEx.Configuration;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: Unity's Time.maximumDeltaTime ships at 0.333 s, so a single long frame is
    // followed by up to ~16 fixed physics steps of catch-up in the NEXT frame - which makes that
    // frame long too. Every hitch this mod has chased is amplified by this spiral: a 300 ms
    // stall (a zone crossing, a shader compile) buys a second frame of pure physics debt.
    //
    // Fix: cap the debt. maximumDeltaTime = N * fixedDeltaTime bounds how many fixed steps one
    // frame may run; time the simulation cannot cover is dropped, exactly as vanilla already
    // drops it past its own (higher) cap. During a capped burst the world advances slightly
    // slower than the wall clock for a few frames - the same trade vanilla makes, at a
    // threshold low enough to matter.
    //
    // Not a Harmony patch: maximumDeltaTime is a global the engine never rewrites mid-session,
    // so binding plus a SettingChanged reapply is the whole mechanism (the Point Light Limit
    // precedent). Provenance: ontrigger's ValheimPerformanceOptimizations (MIT), same default -
    // https://github.com/ontrigger/ValheimPerformanceOptimizations
    //
    // Both: a dedicated server pays the identical catch-up spiral after its own stalls.
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
