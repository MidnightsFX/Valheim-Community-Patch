using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Vanilla defect: Projectile.FixedUpdate passes its velocity to Quaternion.LookRotation unchecked:
    //
    //   if ((double) this.m_rotateVisual == 0.0)
    //     this.transform.rotation = Quaternion.LookRotation(this.m_vel);
    //
    // A projectile whose velocity is exactly zero (drag cancelling gravity, a stalled or attached
    // projectile) makes Unity log "Look rotation viewing vector is zero" every FixedUpdate, per
    // projectile. With a volley in flight that is sustained log spam and a measurable cost, since
    // every one of those messages is formatted and written to disk.
    //
    // Fix: fall back to identity for a zero vector, which is what LookRotation effectively does after
    // logging anyway.
    //
    // Provenance: same fix as ComfyMods/BetterZeeLog (GPL-3.0, redseiko).
    [HarmonyPatch(typeof(Projectile))]
    internal static class ProjectileZeroVelocityPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindServerConfig(
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

        [HarmonyTranspiler]
        [HarmonyPatch("FixedUpdate")]
        private static IEnumerable<CodeInstruction> FixedUpdateTranspiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            if (Enabled == null || !Enabled.Value) { return codes; }

            int patched = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (!codes[i].Calls(LookRotationMethod)) { continue; }
                codes[i].operand = SafeLookRotationMethod;
                patched++;
            }

            if (patched == 0) {
                Logger.LogWarning("Projectile.FixedUpdate: found no LookRotation call to guard; leaving it unpatched.");
                return instructions;
            }

            return codes;
        }
    }
}
