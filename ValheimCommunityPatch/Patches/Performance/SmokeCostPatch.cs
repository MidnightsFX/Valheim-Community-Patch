using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Smoke Overhead: every smoke puff writes its physics mass and has its position read twice, every frame.
    // Prefixes replace Smoke.CustomUpdate and SmokeRenderer.LateUpdate with copies that bucket and throttle that work.
    // Each smoke puff costs fewer engine calls per frame, with no visible change.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Smoke))]
    internal static class SmokeCostPatch {
        private const string FixName = "Fix Smoke Overhead";

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(SmokeCostPatch));

        // Every prefix counts: whichever skipped the update is among them.
        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            typeof(SmokeCostPatch),
            AccessTools.DeclaredMethod(typeof(Smoke), "CustomUpdate"),
            HookKinds.Prefixes,
            owners => $"Smoke puff updates are replaced by {owners}, so '{FixName}' leaves them to that " +
                      "mod. Its smoke rendering change stays on.");

        // Vanilla's CustomUpdate with the mass write bucketed; re-check the copy against the game
        // source on updates. Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("CustomUpdate")]
        private static bool CustomUpdatePrefix(Smoke __instance, float deltaTime, float time, bool __runOriginal) {
            if (!__runOriginal) {
                // Another mod has replaced the update (ValheimPerformanceOverhaul does while its smoke
                // option is on); a second one would age and push the puff twice. Read only for its
                // one-time log line naming the other mod.
                _ = Takeover.TakenOver;
                return false;
            }

            if (ApiSwitch.Off) { return true; }

            __instance.m_time += deltaTime;
            if (__instance.m_time > __instance.m_ttl && __instance.m_fadeTimer < 0.0) {
                __instance.StartFadeOut();
            }

            float num1 = 1f - Mathf.Clamp01(__instance.m_time / __instance.m_ttl);

            // Fiftieths of lifetime, at most 0.04 off vanilla's 0..1 curve; the first tick always
            // writes to cover the prefab default.
            int bucket = (int)(Mathf.Clamp01(__instance.m_time / __instance.m_ttl) * 50f);
            int previousBucket = (int)(Mathf.Clamp01((__instance.m_time - deltaTime) / __instance.m_ttl) * 50f);
            if (bucket != previousBucket || __instance.m_time <= deltaTime) {
                __instance.m_body.mass = num1 * num1;
            }

            Vector3 linearVelocity = __instance.m_body.linearVelocity;
            Vector3 vel = __instance.m_vel;
            vel.y *= num1;
            __instance.m_body.AddForce((vel - linearVelocity) * (__instance.m_force * deltaTime), ForceMode.VelocityChange);

            if (__instance.m_fadeTimer < 0.0) { return false; }

            __instance.m_fadeTimer += deltaTime;
            if (__instance.m_fadeTimer < __instance.m_fadetime) { return false; }

            Object.Destroy(__instance.gameObject);
            return false;
        }

        [HarmonyPatch(typeof(SmokeRenderer))]
        internal static class RendererHook {
            private static float _nextTransfer;

            // Particle positions are world coordinates, so a puff that crossed into another chunk
            // renders the same from its old one until the next transfer.
            private const float TransferInterval = 0.25f;

            // Vanilla's LateUpdate with the transfer scan throttled, one position read per smoke,
            // and the array clamp; re-check the copy against the game source on updates. No other
            // mod is known to replace it, so it stays on when the puff update stands down.
            [HarmonyPrefix]
            [HarmonyPatch("LateUpdate")]
            private static bool LateUpdatePrefix(SmokeRenderer __instance) {
                if (ApiSwitch.Off) { return true; }

                float now = Time.time;
                if (now >= _nextTransfer) {
                    _nextTransfer = now + TransferInterval;
                    __instance.TransferSmokeBetweenChunks();
                }

                foreach (Vector3Int key in __instance.m_chunkedParticleSystems.Keys) {
                    ParticleSystem system = __instance.m_chunkedParticleSystems[key];
                    List<Smoke> smokeList = __instance.m_chunkedSmoke[key];
                    ParticleSystem.Particle[] particles = __instance.m_chunkedParticles[key];

                    if (smokeList.Count > system.particleCount) {
                        system.Emit(smokeList.Count - system.particleCount);
                    }

                    // Vanilla indexes the 100-entry chunk arrays unchecked.
                    int count = Mathf.Min(smokeList.Count, particles.Length);
                    for (int i = 0; i < count; i++) {
                        Smoke smoke = smokeList[i];
                        Vector3 position = smoke.transform.position;

                        // Smoke.GetParticleValues inlined against the position already read,
                        // keeping its writes into m_renderParticle.
                        smoke.m_renderParticle.remainingLifetime = smoke.m_fadeTimer >= 0.0
                            ? smoke.m_fadetime - smoke.m_fadeTimer
                            : smoke.m_ttl - smoke.m_time;
                        smoke.m_renderParticle.position = position;

                        ParticleSystem.Particle particle = smoke.m_renderParticle;
                        particle.startColor = (Color32)(__instance.m_smokeColor * new Color(1f, 1f, 1f, smoke.GetAlpha()));
                        particle.startSize = __instance.m_smokeBallSize;
                        particles[i] = particle;
                    }

                    int active = Mathf.Min(system.particleCount, particles.Length);
                    for (int i = count; i < active; i++) { particles[i].remainingLifetime = -1f; }

                    system.SetParticles(particles, active);
                }

                return false;
            }
        }
    }
}
