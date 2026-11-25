using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Collision Callback Allocation: Valheim ships with collision reuse off, so each callback allocates a Collision.
    // Sets Physics.reuseCollisionCallbacks once at startup, with no Harmony hook, and clears it if turned off by API.
    // Collision callbacks stop producing garbage on every contact.
    [PatchSide(Side.Both)]
    [ModDisableable]
    internal static class CollisionCallbackReusePatch {
        internal static void Apply() {
            if (Physics.reuseCollisionCallbacks) {
                Logger.LogInfo(
                    "Collision callback reuse was already enabled by the game, so 'Fix Collision " +
                    "Callback Allocation' changes nothing on this build.");
                return;
            }

            // No handler may now keep a Collision past its own callback: none of the vanilla ones do,
            // but a mod that stashes one reads a later collision's data through it, so this is the
            // first suspect if a physics mod misbehaves. A contacts array read inside the callback is fine.
            Physics.reuseCollisionCallbacks = true;

            // Turned off through the mod API, for a mod that keeps a Collision past its callback.
            FixRegistry.SwitchOf(typeof(CollisionCallbackReusePatch)).TurnedOff +=
                () => Physics.reuseCollisionCallbacks = false;
        }
    }
}
