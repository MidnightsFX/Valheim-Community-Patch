using System.Diagnostics;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Reflection Probe Spikes: the realtime reflection cubemap renders one face per frame
    // instead of all six in one frame.
    //
    // ReflectionUpdate.UpdateReflection calls ReflectionProbe.RenderProbe, which renders all six
    // cubemap faces inside a single frame every few seconds: a steady cost delivered as periodic
    // single-frame spikes.
    //
    // A prefix replaces the per-frame driver. Faces are rendered one per frame with an explicit
    // camera into this mod's own cubemap render textures, and the probe's realtimeTexture is
    // swapped only when the sixth face completes, so the probe never shows a half-built cubemap
    // and vanilla's two-probe crossfade runs unchanged. While a face renders, quality is clamped
    // (LOD bias, two shadow cascades, 80 m shadows) and characters, items and effects are
    // excluded: a deliberate fidelity trade in a blurry 128px environment reflection. A face is
    // also held back while the previous frame ran over budget, or for a few frames after a face
    // that was itself expensive, and a starvation guard renders regardless after three consecutive
    // defers so the cubemap always finishes well inside vanilla's 3 s refresh interval.
    //
    // Client: probes need a graphics device. Provenance: ontrigger's
    // ValheimPerformanceOptimizations (MIT), reworked from a component swap into a prefix.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(ReflectionUpdate))]
    internal static class ReflectionSlicePatch {
        internal static ConfigEntry<int> Resolution;
        internal static ConfigEntry<int> FrameBudgetMs;

        internal static void BindConfig() {
            // Client-local visual preferences, deliberately not server-synced.
            Resolution = ValConfig.cfg.Bind(
                "Client config",
                "Reflection Resolution",
                128,
                new ConfigDescription(
                    "Cubemap face resolution for the sliced reflection renderer. Higher is " +
                    "sharper reflections and more per-face cost.",
                    new AcceptableValueList<int>(64, 128, 256, 512),
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

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
        private const int MaxConsecutiveDefers = 3;
        private const int CooldownFrames = 4;

        private static Camera _camera;
        private static RenderTexture _cube1;
        private static RenderTexture _cube2;
        private static int _nextFace = FaceIdle;
        private static int _deferStreak;
        private static int _cooldownFrames;
        private static bool _finished;
        private static Vector3 _renderPosition;
        private static int _excludeMask;
        private static float[] _layerCullDistances;

        // The timer, probe swap and crossfade replicate vanilla's Update; only the render is sliced.
        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        private static bool UpdatePrefix(ReflectionUpdate __instance) {
            __instance.m_updateTimer += Time.deltaTime;

            if (_nextFace == FaceIdle && __instance.m_updateTimer > __instance.m_interval) {
                __instance.m_updateTimer = 0.0f;
                BeginRender(__instance);
            }

            if (_nextFace >= 0 && !DeferFace()) {
                long started = Stopwatch.GetTimestamp();
                RenderFace(__instance, _nextFace);

                // A face that cost more than the frame budget on its own arms the cooldown.
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
                // Vanilla's crossfade.
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

        // The public one-shot entry point starts a sliced cycle instead.
        [HarmonyPrefix]
        [HarmonyPatch("UpdateReflection")]
        private static bool UpdateReflectionPrefix(ReflectionUpdate __instance) {
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

        // Holds a face back while the previous frame is over budget or a cooldown is armed, and
        // gives up on holding after MaxConsecutiveDefers so the cubemap always finishes.
        private static bool DeferFace() {
            int budget = FrameBudgetMs != null ? FrameBudgetMs.Value : 0;

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
    }
}
