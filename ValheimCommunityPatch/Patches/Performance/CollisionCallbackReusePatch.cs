using BepInEx.Configuration;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: Unity allocates a fresh Collision object for every collision callback it
    // dispatches, and throws it away the moment the callback returns. Physics.reuseCollisionCallbacks
    // makes the engine hand the same instance back each time instead; it exists precisely because the
    // allocation is almost always pure waste, and new Unity projects have shipped with it on by
    // default since 2018.3.
    //
    // The reach is wider than the contact-array fix next door. Unity allocates the Collision before
    // the callback body runs, so it is paid for every collision on every object in the scene that
    // declares a handler, whoever owns it - Character.OnCollisionStay allocates and then immediately
    // returns on !m_nview.IsOwner() (Character.cs:1674-1676) for every character the machine did not
    // own. Character, Aoe, Fish, FloatingTerrain, FloatingTerrainDummy and ImpactEffect all declare
    // one.
    //
    // Fix: turn the setting on. The one rule it imposes is that nobody may keep a Collision past the
    // end of its callback, since the next one overwrites it. Every vanilla handler obeys that today,
    // checked one by one: Aoe passes collision.collider straight to CauseTriggerDamage (Aoe.cs:386-394);
    // Character copies contact points and stores collision.collider, a Collider reference rather than
    // the Collision (Character.cs:1674-1706); Fish only counts (Fish.cs:497-499); FloatingTerrainDummy
    // forwards synchronously to FloatingTerrain, which reads one normal (FloatingTerrainDummy.cs:14-19,
    // FloatingTerrain.cs:93-112); ImpactEffect reads relativeVelocity and contacts[0] into locals
    // before doing any work (ImpactEffect.cs:39-70).
    //
    // What it cannot check is other mods. A mod that stashes a Collision for later reads whatever the
    // next collision wrote, so this is the one fix here whose blast radius is outside the mod - hence
    // its own toggle and this note. If a physics-touching mod starts behaving strangely, turn this off
    // first.
    //
    // Not a Harmony patch: this is a global the engine never rewrites mid-session, so binding plus a
    // SettingChanged reapply is the whole mechanism - the PhysicsCatchupPatch precedent. The value the
    // project shipped with is captured before the first write and restored if the fix is switched off,
    // so a Valheim build that already enables this is left exactly as it was.
    //
    // Provenance: R4V9N1's Terramizer sets the same flag from its own config.
    //
    // Both: collision callbacks are dispatched by the physics engine wherever it runs, and a dedicated
    // server simulates whatever falls inside its own active area.
    [PatchSide(Side.Both)]
    internal static class CollisionCallbackReusePatch {
        internal static ConfigEntry<bool> Enabled;

        private static bool _vanillaCaptured;
        private static bool _vanillaReuse;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(CollisionCallbackReusePatch),
                ValConfig.SectionPerformance,
                "Fix Collision Callback Allocation",
                true,
                "Reuses one collision-callback object instead of allocating a fresh one for every " +
                "collision on every object in the scene. Every vanilla handler is safe with this and " +
                "the physics data they receive is unchanged. Turn it off if a mod that reads physics " +
                "collisions misbehaves - a mod that keeps a collision object past the end of its own " +
                "callback would read the next collision's data.");

            Apply();
            Enabled.SettingChanged += (sender, args) => Apply();
        }

        private static void Apply() {
            if (!_vanillaCaptured) {
                _vanillaReuse = Physics.reuseCollisionCallbacks;
                _vanillaCaptured = true;

                if (_vanillaReuse) {
                    // Worth one line in the log: it means this fix is a no-op on this build, and
                    // that is a useful thing to know before attributing a measurement to it.
                    Logger.LogInfo(
                        "Collision callback reuse was already enabled by the game, so 'Fix Collision " +
                        "Callback Allocation' changes nothing on this build.");
                }
            }

            Physics.reuseCollisionCallbacks =
                Enabled != null && Enabled.Value ? true : _vanillaReuse;
        }
    }
}
