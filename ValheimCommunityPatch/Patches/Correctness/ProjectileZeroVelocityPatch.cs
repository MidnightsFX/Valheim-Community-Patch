using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Projectile Rotation Spam: stops the "Look rotation viewing vector is zero" warning that
    // every stalled projectile logs on every physics step.
    //
    // Projectile.FixedUpdate passes its velocity to Quaternion.LookRotation unchecked. A projectile
    // whose velocity reaches exactly zero makes Unity log the warning every FixedUpdate, and each
    // message is formatted, stack-traced and written to disk.
    //
    // A transpiler swaps the LookRotation call for one that returns identity for a zero vector,
    // which is what LookRotation does anyway after logging.
    //
    // Both: FixedUpdate runs on whoever owns the projectile. Provenance: the same fix as
    // ComfyMods/BetterZeeLog (GPL-3.0, redseiko); where both are installed its rewrite lands first
    // and this one finds nothing to do, which is the intended outcome.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(Projectile))]
    internal static class ProjectileZeroVelocityPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ProjectileZeroVelocityPatch),
                ValConfig.SectionCorrectness,
                "Fix Projectile Rotation Spam",
                true,
                "Stops the 'Look rotation viewing vector is zero' log spam produced every physics step by " +
                "each projectile whose velocity reaches exactly zero. Changing this requires a game restart.");
        }

        private static readonly MethodInfo LookRotationMethod =
            AccessTools.Method(typeof(Quaternion), nameof(Quaternion.LookRotation), new[] { typeof(Vector3) });
        private static readonly MethodInfo SafeLookRotationMethod =
            AccessTools.Method(typeof(ProjectileZeroVelocityPatch), nameof(SafeLookRotation));

        private static Quaternion SafeLookRotation(Vector3 forward) =>
            forward == Vector3.zero ? Quaternion.identity : Quaternion.LookRotation(forward);

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("FixedUpdate")]
        private static IEnumerable<CodeInstruction> FixedUpdateTranspiler(IEnumerable<CodeInstruction> instructions) {
            if (Enabled == null || !Enabled.Value) { return instructions; }

            return PatchHelper.ReplaceCalls(instructions, LookRotationMethod, SafeLookRotationMethod, "Projectile.FixedUpdate");
        }
    }
}
