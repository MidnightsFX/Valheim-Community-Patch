using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // A projectile at zero velocity makes Quaternion.LookRotation log a stack-traced warning every physics step.
    // Transpiles Projectile.FixedUpdate to return identity for a zero vector, as LookRotation does after logging.
    // Stalled projectiles no longer flood the log with "Look rotation viewing vector is zero".
    // Provenance: the same fix as ComfyMods/BetterZeeLog (GPL-3.0, redseiko).
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(Projectile))]
    internal static class ProjectileZeroVelocityPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ProjectileZeroVelocityPatch),
                ValConfig.SectionCorrectness,
                "Fix Projectile Rotation Spam",
                true,
                "Stops the 'Look rotation viewing vector is zero' log spam from projectiles stalled at zero " +
                "velocity; turning it on needs a game restart.");
        }

        private static readonly MethodInfo LookRotationMethod =
            AccessTools.Method(typeof(Quaternion), nameof(Quaternion.LookRotation), new[] { typeof(Vector3) });
        private static readonly MethodInfo SafeLookRotationMethod =
            AccessTools.Method(typeof(ProjectileZeroVelocityPatch), nameof(SafeLookRotation));

        private static Quaternion SafeLookRotation(Vector3 forward) =>
            forward == Vector3.zero && Enabled.Value ? Quaternion.identity : Quaternion.LookRotation(forward);

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches. With BetterZeeLog installed its rewrite
        // lands first and this finds no call to replace, as intended.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("FixedUpdate")]
        private static IEnumerable<CodeInstruction> FixedUpdateTranspiler(IEnumerable<CodeInstruction> instructions) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            return PatchHelper.ReplaceCalls(instructions, LookRotationMethod, SafeLookRotationMethod, "Projectile.FixedUpdate");
        }
    }
}
