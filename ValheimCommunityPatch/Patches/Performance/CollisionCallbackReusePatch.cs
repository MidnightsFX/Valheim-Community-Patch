using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Collision Callback Allocation: turns on Physics.reuseCollisionCallbacks so Unity stops
    // allocating a Collision object for every collision callback.
    //
    // Unity allocates a fresh Collision per callback and drops it when the callback returns. The
    // allocation happens before the handler body runs, so it is paid even by handlers that return
    // immediately on !IsOwner(). New Unity projects have shipped with reuse on since 2018.3.
    //
    // One write at startup. The rule it imposes is that no handler may keep a Collision past its
    // own callback. Every vanilla handler (Aoe, Character, Fish, FloatingTerrain,
    // FloatingTerrainDummy, ImpactEffect) was checked and none does. A mod that stashes a
    // Collision for later would read the next collision's data, so this is the first suspect if a
    // physics-touching mod misbehaves. A build that already has it on is left alone.
    //
    // Both: the physics engine dispatches callbacks wherever it runs.
    [PatchSide(Side.Both)]
    internal static class CollisionCallbackReusePatch {
        internal static void Apply() {
            if (Physics.reuseCollisionCallbacks) {
                Logger.LogInfo(
                    "Collision callback reuse was already enabled by the game, so 'Fix Collision " +
                    "Callback Allocation' changes nothing on this build.");
                return;
            }

            Physics.reuseCollisionCallbacks = true;
        }
    }
}
