using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect, measured over an hour in a torch-and-fire-heavy base: the smoke system cost
    // ~103 s of CPU (~29 ms of every second), almost all of it per-smoke-per-frame engine calls
    // that buy nothing visible:
    //
    //  - Smoke.CustomUpdate writes m_body.mass every frame (Smoke.cs:137) - a native physics
    //    write per smoke per frame, 20 s of the hour alone - even though the value is a smooth
    //    curve of the smoke's age and drifts by a fraction of a percent between frames.
    //  - SmokeRenderer.LateUpdate reads every smoke's transform.position TWICE per frame: once
    //    in TransferSmokeBetweenChunks to recheck which 10 m render chunk it belongs to
    //    (Smoke.cs rises ~1-2 m/s, so the answer changes once every several seconds), and again
    //    inside GetParticleValues when building the particle arrays (SmokeRenderer.cs:101,
    //    Smoke.cs:56).
    //
    // Fix, three parts, all inside faithful replicas of the two methods:
    //
    //  - The mass write is quantized to lifetime fiftieths: mass is (1 - t/ttl)^2, so writing on
    //    each 2% step of lifetime bounds the drift from vanilla at 0.04 mass on a 0..1 curve -
    //    imperceptible against smoke's random drift - and drops the native writes ~10x. The
    //    bucket is derived from m_time alone, so there is no per-smoke bookkeeping to leak. The
    //    force integration below it is untouched and still runs every frame.
    //  - The chunk-transfer scan runs at 4 Hz instead of every frame. Particle positions are
    //    absolute world coordinates, so a smoke that crossed a chunk boundary renders identically
    //    from the old chunk's particle system for up to a quarter second; chunk membership only
    //    decides which batch carries it.
    //  - The render loop reads each smoke's position once and feeds it both to the particle it
    //    builds and to the same m_renderParticle writes vanilla's GetParticleValues performs, so
    //    fade-state stays byte-identical for anything else that reads it.
    //
    // The render loop also clamps to the 100-entry chunk arrays. Vanilla indexes them by raw
    // list count and relies on the spawners' global smoke cap to stay under 100 per chunk;
    // deferring transfers widens that latent window slightly, so the replica closes it instead
    // of inheriting it.
    //
    // Replicated wholesale like RemoveObjectsNrePatch's sibling: re-check against the game
    // source on updates; other mods' prefixes on these two methods are bypassed while this is
    // on (postfixes still run).
    //
    // Client: smoke exists to be rendered; nothing headless runs these paths with a camera.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(Smoke))]
    internal static class SmokeCostPatch {
        // Vanilla's CustomUpdate verbatim (Smoke.cs:131-149) with the mass write bucketed.
        [HarmonyPrefix]
        [HarmonyPatch("CustomUpdate")]
        private static bool CustomUpdatePrefix(Smoke __instance, float deltaTime, float time) {
            __instance.m_time += deltaTime;
            if (__instance.m_time > __instance.m_ttl && __instance.m_fadeTimer < 0.0) {
                __instance.StartFadeOut();
            }

            float num1 = 1f - Mathf.Clamp01(__instance.m_time / __instance.m_ttl);

            // Fiftieths of lifetime, so the write cadence and the drift bound are independent of
            // this smoke's ttl. The seed write on the first tick covers the prefab default.
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

            // 4 Hz is over twice per chunk-width even for fade-rushed smoke; see header for why
            // stale membership renders identically.
            private const float TransferInterval = 0.25f;

            // Vanilla's LateUpdate verbatim (SmokeRenderer.cs:113-134) with the transfer scan
            // throttled, one position read per smoke, and the array clamp.
            [HarmonyPrefix]
            [HarmonyPatch("LateUpdate")]
            private static bool LateUpdatePrefix(SmokeRenderer __instance) {
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

                    int count = Mathf.Min(smokeList.Count, particles.Length);
                    for (int i = 0; i < count; i++) {
                        Smoke smoke = smokeList[i];
                        Vector3 position = smoke.transform.position;

                        // Smoke.GetParticleValues (Smoke.cs:53-58) inlined against the position
                        // already read, keeping its writes into m_renderParticle.
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
