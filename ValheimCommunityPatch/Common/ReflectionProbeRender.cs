using UnityEngine;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>
    /// Has ReflectionUpdate render a probe on its next Update, or takes one it has just rendered, and
    /// shows the capture at full size as soon as it finishes, instead of growing it in over the
    /// transition. Shared by Fix Lighting
    /// After Teleport and Fix Lightning Flash Lighting, and driven from the Update hook in
    /// TeleportLightingPatch.
    /// </summary>
    /// <remarks>
    /// Nothing is held in the game object: Force only sets the timer that vanilla puts back to 0 when
    /// it renders, and the window writes the box size that vanilla rewrites on every frame it passes
    /// its own gate, so a turn-off, an instance change or a crash leaves nothing to restore.
    /// </remarks>
    internal static class ReflectionProbeRender {
        // Vanilla's full box, which Update grows towards as Pow(timer / transition, power).
        private static readonly Vector3 FullSize = new Vector3(2000f, 1000f, 2000f);
        // Keeps a held timer under the interval after vanilla adds the frame's delta to it.
        private const float HoldSlack = 0.001f;

        private static bool _pending;
        private static int _renderId = -1;
        private static ReflectionProbe _probe;
        private static int _tickFrame = -1;

        /// <summary>A forced render is waiting for the next Update to issue it.</summary>
        internal static bool PendingForce => _pending;

        /// <summary>
        /// Vanilla renders when timer + deltaTime exceeds the interval. Infinity makes that true even
        /// on a frame with no delta, which an interval-sized timer would not, and vanilla sets the
        /// timer back to 0 when it renders.
        /// </summary>
        internal static void Force(ReflectionUpdate r) {
            r.m_updateTimer = float.PositiveInfinity;
            _pending = true;
        }

        /// <summary>
        /// Treats the render just issued outside vanilla's timer as a forced one: the cycle restarts
        /// from it, and it shows at full size as soon as it finishes.
        /// </summary>
        internal static void Adopt(ReflectionUpdate r) {
            r.m_updateTimer = 0f;
            _pending = false;
            _renderId = r.m_renderID;
            _probe = r.m_current;
        }

        /// <summary>
        /// Keeps vanilla from rendering this frame: after it adds the frame's delta the timer still
        /// sits just under the interval. A render not due this frame is left alone.
        /// </summary>
        internal static void Hold(ReflectionUpdate r, float dt) {
            if (r.m_updateTimer + dt > r.m_interval) {
                r.m_updateTimer = r.m_interval - dt - HoldSlack;
            }
        }

        /// <summary>Drops a pending force and the full-size window.</summary>
        internal static void Clear() {
            _pending = false;
            _renderId = -1;
            _probe = null;
        }

        /// <summary>
        /// Run after Update. Records the render a force produced, then keeps that probe's box full
        /// until vanilla's own growth has reached full size or another render has taken over.
        /// </summary>
        internal static void Tick(ReflectionUpdate r) {
            int frame = Time.frameCount;
            if (_tickFrame == frame) { return; }
            _tickFrame = frame;

            if (_pending) {
                // Still infinite: another mod skipped this Update, so the render is still to come.
                if (float.IsPositiveInfinity(r.m_updateTimer)) { return; }
                _pending = false;
                _renderId = r.m_renderID;
                _probe = r.m_current;
            }

            if (ReferenceEquals(_probe, null)) { return; }

            if (_renderId != r.m_renderID
                || !ReferenceEquals(_probe, r.m_current)
                || r.m_updateTimer >= r.m_transitionDuration) {
                _renderId = -1;
                _probe = null;
                return;
            }

            // Vanilla shows a capture, and sizes its box, only once every face is rendered.
            if (!_probe.IsFinishedRendering(_renderId)) { return; }

            _probe.size = FullSize;
        }
    }
}
