using System.Diagnostics;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: the realtime reflection probes render ALL SIX cubemap faces inside a single
    // frame every few seconds (ReflectionUpdate.UpdateReflection -> ReflectionProbe.RenderProbe,
    // surfacing as BuiltinRuntimeReflectionSystem.TickRealtimeProbes) - measured at a steady
    // 14-18 ms of every second across sessions, delivered as periodic single-frame spikes.
    //
    // Fix: render ONE face per frame with an explicit camera into our own cubemap render
    // texture, publishing to the probe's realtimeTexture only when the sixth face completes -
    // the same double-probe crossfade vanilla runs, spread over six frames. While a face
    // renders, quality is temporarily clamped (LOD bias, two shadow cascades, 80 m shadow
    // distance, LOD level 1) and characters, items, effects and TransparentFX are excluded from
    // the reflection - reflections are a blurry 128px environment cube, where none of that
    // reads, but it IS a deliberate fidelity trade and the description says so.
    //
    // Slicing alone still dropped a fixed ~10 ms face onto whatever frame came next, and a face
    // landing on a frame the streaming system was already saturating was measured tipping
    // otherwise-marginal frames past the spike threshold. So a face is also DEFERRED while the
    // previous frame is over budget: nothing consumes a half-built cubemap - the probe keeps
    // showing the previous one until the sixth face publishes, and vanilla's own cadence already
    // leaves a reflection up to m_interval (3 s) stale - so a defer changes only WHICH frame
    // pays for the face, never what is displayed. Deferring stretches a cycle by a few frames,
    // which starts the 3 s crossfade a few percent further along; invisible at this cadence.
    //
    // Reading the previous frame is not enough on its own, because it cannot see a face that is
    // itself expensive. Measured in a large base, a face is usually ~4 ms but occasionally
    // 100-150 ms inside Camera.RenderToCubemap, and those land on frames that looked perfectly
    // healthy a moment earlier. So each face is TIMED, and one that blew the budget arms a short
    // cooldown - the same two-signal pacing ZoneGenPacingPatch uses, for the same reason: the
    // expensive renders cluster (same camera position, same heavy scene), so spacing the rest of
    // that cycle out is exactly what is wanted.
    //
    // The starvation guard is load-bearing rather than hygiene: without it a machine that never
    // makes budget would never publish its first cubemap at all. It bounds the cooldown too, so
    // a permanently expensive scene still finishes its cubemap - a face every
    // (MaxConsecutiveDefers + 1) frames, ~24 frames for a full cycle, still comfortably inside
    // the 3 s refresh interval.
    //
    // Provenance: technique from ontrigger's ValheimPerformanceOptimizations (MIT),
    // https://github.com/ontrigger/ValheimPerformanceOptimizations - reworked
    // from a component swap into this mod's prefix style so the toggle works at runtime: on
    // toggle-off the probes' realtimeTexture is handed back and vanilla's RenderProbe path
    // resumes on its own timer.
    //
    // Client: probes only exist with a graphics device.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(ReflectionUpdate))]
    internal static class ReflectionSlicePatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> Resolution;
        internal static ConfigEntry<int> FrameBudgetMs;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ReflectionSlicePatch),
                ValConfig.SectionPerformance,
                "Fix Reflection Probe Spikes",
                true,
                "Renders the realtime reflection cubemap one face per frame instead of all six " +
                "in one frame - the periodic reflection spike becomes six small slices. " +
                "Reflections render at reduced quality (lower LOD, shorter shadows, no " +
                "characters or items) - a deliberate trade that is hard to spot in a blurry " +
                "environment reflection.");

            // Client-local visual preference, deliberately not server-synced.
            Resolution = ValConfig.cfg.Bind(
                "Client config",
                "Reflection Resolution",
                128,
                new ConfigDescription(
                    "Cubemap face resolution for the sliced reflection renderer. Higher is " +
                    "sharper reflections and more per-face cost.",
                    new AcceptableValueList<int>(64, 128, 256, 512),
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            // Client-local for the same reason as Resolution: a dedicated server has no probes,
            // so there is nothing here for a server to sync down.
            FrameBudgetMs = ValConfig.cfg.Bind(
                "Client config",
                "Reflection Frame Budget",
                33,
                new ConfigDescription(
                    "Milliseconds: a reflection face is held back when the previous frame ran " +
                    "longer than this, so it lands on a quiet frame instead of piling onto one " +
                    "that was already struggling. The reflection is never shown half-built - " +
                    "only the frame that pays for a face moves. 0 renders a face every frame.",
                    new AcceptableValueRange<int>(0, 100),
                    new ConfigurationManagerAttributes { IsAdvanced = true }));
        }

        private const int FaceIdle = -1;

        // After this many consecutive defers the face renders regardless (see header).
        private const int MaxConsecutiveDefers = 3;

        // Frames to hold off after a face that blew the budget on its own.
        private const int CooldownFrames = 4;

        private static Camera _camera;
        private static RenderTexture _cube1;
        private static RenderTexture _cube2;
        private static int _nextFace = FaceIdle;
        private static int _deferStreak;
        private static int _cooldownFrames;
        private static bool _finished;
        private static Vector3 _renderPosition;
        private static bool _tookOver;
        private static int _excludeMask;
        private static float[] _layerCullDistances;

        // Replaces the per-frame driver. The timer, probe swap and crossfade replicate vanilla
        // (ReflectionUpdate.cs:41-76); only the render itself is sliced.
        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        private static bool UpdatePrefix(ReflectionUpdate __instance) {
            if (Enabled == null || !Enabled.Value) {
                ReleaseTakeover(__instance);
                return true;
            }

            _tookOver = true;
            __instance.m_updateTimer += Time.deltaTime;

            if (_nextFace == FaceIdle && __instance.m_updateTimer > __instance.m_interval) {
                __instance.m_updateTimer = 0.0f;
                BeginRender(__instance);
            }

            if (_nextFace >= 0 && !DeferFace()) {
                long started = Stopwatch.GetTimestamp();
                RenderFace(__instance, _nextFace);

                // A face that cost more than the whole frame budget on its own arms the cooldown;
                // the next few frames hold off so the rest of this cycle spreads out.
                double elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                int spent = FrameBudgetMs != null ? FrameBudgetMs.Value : 0;
                if (spent > 0 && elapsedMs > spent) { _cooldownFrames = CooldownFrames; }

                _nextFace++;
                if (_nextFace > (int)CubemapFace.NegativeZ) {
                    _nextFace = FaceIdle;
                    _finished = true;
                    Current(__instance).realtimeTexture = TargetFor(__instance);
                }
            }

            if (_finished) {
                // Vanilla's crossfade block verbatim.
                float num = Mathf.Pow(Mathf.Clamp01(__instance.m_updateTimer / __instance.m_transitionDuration), __instance.m_power);
                if (__instance.m_probe1 == Current(__instance)) {
                    __instance.m_probe1.importance = 1;
                    __instance.m_probe2.importance = 0;
                    __instance.m_probe1.size = new Vector3(2000f * num, 1000f * num, 2000f * num);
                    __instance.m_probe2.size = new Vector3(2001f, 1001f, 2001f);
                } else {
                    __instance.m_probe1.importance = 0;
                    __instance.m_probe2.importance = 1;
                    __instance.m_probe2.size = new Vector3(2000f * num, 1000f * num, 2000f * num);
                    __instance.m_probe1.size = new Vector3(2001f, 1001f, 2001f);
                }
            }

            return false;
        }

        // Anything calling the public one-shot entry point starts a sliced cycle instead.
        [HarmonyPrefix]
        [HarmonyPatch("UpdateReflection")]
        private static bool UpdateReflectionPrefix(ReflectionUpdate __instance) {
            if (Enabled == null || !Enabled.Value) { return true; }

            __instance.m_updateTimer = 0.0f;
            BeginRender(__instance);
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch("OnDestroy")]
        private static void OnDestroyPostfix() {
            if (_cube1 != null) { _cube1.Release(); _cube1 = null; }
            if (_cube2 != null) { _cube2.Release(); _cube2 = null; }
            _camera = null;
            _nextFace = FaceIdle;
            _deferStreak = 0;
            _finished = false;
            _tookOver = false;
        }

        private static ReflectionProbe Current(ReflectionUpdate update) => update.m_current;

        private static RenderTexture TargetFor(ReflectionUpdate update) =>
            update.m_current == update.m_probe1 ? _cube1 : _cube2;

        private static void BeginRender(ReflectionUpdate update) {
            EnsureResources(update);

            if (_nextFace == FaceIdle) {
                update.m_current = update.m_current == update.m_probe1 ? update.m_probe2 : update.m_probe1;
            }

            _renderPosition = ZNet.instance.GetReferencePosition() + Vector3.up * update.m_reflectionHeight;
            update.m_current.transform.position = _renderPosition;
            _nextFace = (int)CubemapFace.PositiveX;
            _finished = false;
            _deferStreak = 0;
            _cooldownFrames = 0;
        }

        private static void EnsureResources(ReflectionUpdate update) {
            if (_camera == null) {
                _camera = update.gameObject.GetComponent<Camera>();
                if (_camera == null) { _camera = update.gameObject.AddComponent<Camera>(); }
                _camera.enabled = false;
                _camera.farClipPlane = 1000f;

                _excludeMask = (1 << LayerMask.NameToLayer("character"))
                    | (1 << LayerMask.NameToLayer("effect"))
                    | (1 << LayerMask.NameToLayer("item"))
                    | (1 << LayerMask.NameToLayer("TransparentFX"));
                _layerCullDistances = new float[32];
                _layerCullDistances[LayerMask.NameToLayer("piece")] = 500f;
            }

            int size = Resolution != null ? Resolution.Value : 128;
            if (_cube1 == null || _cube1.width != size) {
                if (_cube1 != null) { _cube1.Release(); }
                if (_cube2 != null) { _cube2.Release(); }
                _cube1 = CreateCube(size);
                _cube2 = CreateCube(size);
            }
        }

        private static RenderTexture CreateCube(int size) {
            return new RenderTexture(size, size, 16) {
                dimension = TextureDimension.Cube, useMipMap = true, autoGenerateMips = true,
            };
        }

        // Holds a face back while the PREVIOUS frame is over budget - the same pacing signal
        // ZoneGenPacingPatch reads - and gives up on holding after MaxConsecutiveDefers so the
        // cubemap always finishes (see header).
        private static bool DeferFace() {
            int budget = FrameBudgetMs != null ? FrameBudgetMs.Value : 0;

            // The guard outranks both signals, so the cubemap always finishes.
            if (budget <= 0 || _deferStreak >= MaxConsecutiveDefers) {
                _deferStreak = 0;
                _cooldownFrames = 0;
                return false;
            }

            bool wantDefer = Time.unscaledDeltaTime * 1000f > budget || _cooldownFrames > 0;
            if (!wantDefer) {
                _deferStreak = 0;
                return false;
            }

            if (_cooldownFrames > 0) { _cooldownFrames--; }
            _deferStreak++;
            return true;
        }

        private static void RenderFace(ReflectionUpdate update, int face) {
            _camera.transform.position = _renderPosition;

            float oldLodBias = QualitySettings.lodBias;
            int oldCascades = QualitySettings.shadowCascades;
            float oldShadowDistance = QualitySettings.shadowDistance;
            int oldMaxLod = QualitySettings.maximumLODLevel;

            try {
                QualitySettings.lodBias = 5f;
                QualitySettings.shadowCascades = 2;
                QualitySettings.shadowDistance = 80f;
                QualitySettings.maximumLODLevel = 1;

                _camera.cullingMask = update.m_probe1.cullingMask & ~_excludeMask;
                _camera.layerCullDistances = _layerCullDistances;
                _camera.RenderToCubemap(TargetFor(update), 1 << face);
            } finally {
                QualitySettings.lodBias = oldLodBias;
                QualitySettings.shadowCascades = oldCascades;
                QualitySettings.shadowDistance = oldShadowDistance;
                QualitySettings.maximumLODLevel = oldMaxLod;
            }
        }

        // Toggled off mid-session: hand the probes back so vanilla's RenderProbe repopulates
        // them on its own timer instead of sampling our stale low-res cubes forever.
        private static void ReleaseTakeover(ReflectionUpdate update) {
            if (!_tookOver) { return; }
            _tookOver = false;
            _nextFace = FaceIdle;
            _deferStreak = 0;
            _finished = false;
            if (update.m_probe1 != null) { update.m_probe1.realtimeTexture = null; }
            if (update.m_probe2 != null) { update.m_probe2.realtimeTexture = null; }
        }
    }
}
