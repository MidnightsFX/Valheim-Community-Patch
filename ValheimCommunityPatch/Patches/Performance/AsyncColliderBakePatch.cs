using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Zone Collider Stall: an already-generated zone entering the active ring bakes its
    // terrain collider on a background thread instead of the main thread.
    //
    // Assigning a MeshCollider's sharedMesh cooks the PhysX mesh synchronously, and
    // Heightmap.RebuildCollisionMesh does that on every zone heightmap rebuild. For a zone that
    // already exists and is only being loaded because it entered the ring, that cook is the
    // largest single item in the frame and most of the zone-boundary stutter.
    //
    // For two cases only, the prefix hides the collider so vanilla's rebuild skips the
    // assignment, the postfix queues Physics.BakeMesh on a worker thread with the collider's own
    // cooking options, and a MonoUpdaters.LateUpdate postfix assigns sharedMesh once the bake is
    // done, which then finds it cached. The cases are SpawnMode.Client zone spawns and the
    // delayed-poke rebuilds TerrainModifier already deferred a frame, both away from the player's
    // own zone. Everything else (fresh generation, terraforming, the player's zone, spawns with no
    // local player) keeps the synchronous cook because callers rely on the collider in the same
    // frame. A rebuild or OnDestroy on a map with a bake in flight waits for it, because the worker
    // is reading the very mesh they mutate or destroy, and a rebuild assigns that finished bake
    // first so the collider never falls back to an older shape. Heightmap.ForceGenerateAll, which
    // ships, carts and SnapToGround call to make terrain collision current, finishes every bake in
    // flight before it runs and cooks its own rebuilds synchronously.
    //
    // Client: a dedicated server never takes either deferred path.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(Heightmap))]
    internal static class AsyncColliderBakePatch {
        private sealed class PendingBake {
            public Heightmap m_hmap;
            public MeshCollider m_collider;
            public Mesh m_mesh;
            public int m_meshId;
            // The sharedMesh assignment only reuses a bake made with matching options; the
            // parameterless BakeMesh uses Unity's defaults, which differ, and would re-cook.
            public MeshColliderCookingOptions m_options;
            public volatile bool m_done;
        }

        // Main thread only; the worker writes m_done and nothing else.
        private static readonly List<PendingBake> Pending = new List<PendingBake>();
        private static bool _deferContext;
        private static bool _lateUpdateContext;
        private static bool _forceContext;

        private static PendingBake FindPending(Heightmap hmap) {
            for (int i = 0; i < Pending.Count; i++) {
                if (ReferenceEquals(Pending[i].m_hmap, hmap)) { return Pending[i]; }
            }

            return null;
        }

        // The bake itself is short (a 65x65 grid); this mostly waits out the ThreadPool pickup.
        private static void WaitOut(PendingBake bake) {
            while (!bake.m_done) { Thread.Sleep(0); }
        }

        // The zone can be torn down inside the window.
        private static void Assign(PendingBake bake) {
            if (bake.m_hmap == null || bake.m_collider == null) { return; }

            bake.m_collider.sharedMesh = bake.m_mesh;
        }

        private static bool InReferenceZone(Heightmap hmap) {
            return ZNet.instance != null
                && ZoneSystem.GetZone(hmap.transform.position) == ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
        }

        [HarmonyPatch(typeof(ZoneSystem), "SpawnZone")]
        internal static class SpawnZoneContextHook {
            [HarmonyPrefix]
            private static void Prefix(Vector2s zoneID, ZoneSystem.SpawnMode mode) {
                _deferContext =
                    mode == ZoneSystem.SpawnMode.Client
                    && Player.m_localPlayer != null
                    && ZNet.instance != null
                    && zoneID != ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
            }

            // Finalizer rather than postfix so an exception inside SpawnZone cannot leave the
            // context latched.
            [HarmonyFinalizer]
            private static void Finalizer() => _deferContext = false;
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.CustomLateUpdate))]
        internal static class LateUpdateContextHook {
            [HarmonyPrefix]
            private static void Prefix() {
                _lateUpdateContext = Player.m_localPlayer != null;
            }

            [HarmonyFinalizer]
            private static void Finalizer() => _lateUpdateContext = false;
        }

        [HarmonyPrefix]
        [HarmonyPatch("RebuildCollisionMesh")]
        private static void RebuildCollisionMeshPrefix(Heightmap __instance, out MeshCollider __state) {
            __state = null;

            // The finished bake is assigned before this rebuild changes the mesh, which a deferred
            // rebuild would otherwise leave the collider without until its own bake lands. The cook
            // is already cached, so the assignment is cheap.
            PendingBake inflight = FindPending(__instance);
            if (inflight != null) {
                WaitOut(inflight);
                Pending.Remove(inflight);
                Assign(inflight);
            }

            if ((!_deferContext && !_lateUpdateContext) || _forceContext) { return; }
            if (__instance.m_collider == null) { return; }

            // A location arriving in the player's zone pokes it too; the player needs that collider now.
            if (_lateUpdateContext && InReferenceZone(__instance)) { return; }

            // Vanilla's `if (m_collider)` around the assignment does the skipping; hiding the
            // collider lets the rest of the method run unmodified.
            __state = __instance.m_collider;
            __instance.m_collider = null;
        }

        [HarmonyPostfix]
        [HarmonyPatch("RebuildCollisionMesh")]
        private static void RebuildCollisionMeshPostfix(Heightmap __instance, MeshCollider __state) {
            if (__state == null) { return; }

            __instance.m_collider = __state;

            Mesh mesh = __instance.m_collisionMesh;
            if (mesh == null) { return; }

            PendingBake bake = new PendingBake {
                m_hmap = __instance,
                m_collider = __state,
                m_mesh = mesh,
                m_meshId = mesh.GetInstanceID(),
                m_options = __state.cookingOptions,
            };
            Pending.Add(bake);

            ThreadPool.QueueUserWorkItem(_ => {
                try { Physics.BakeMesh(bake.m_meshId, false, bake.m_options); } finally { bake.m_done = true; }
            });
        }

        // Harmony skips the postfix when the rebuild throws, and a collider left hidden would
        // never see another terrain change.
        [HarmonyFinalizer]
        [HarmonyPatch("RebuildCollisionMesh")]
        private static void RebuildCollisionMeshFinalizer(Heightmap __instance, MeshCollider __state) {
            if (__state != null && __instance.m_collider == null) { __instance.m_collider = __state; }
        }

        // Vanilla's way to make terrain collision current before physics or snapping only knows
        // about queued rebuilds, so the bakes in flight are finished first, and the rebuilds it runs
        // cook synchronously even when a ship or cart calls it from inside a deferred zone spawn.
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.ForceGenerateAll))]
        internal static class ForceGenerateAllHook {
            [HarmonyPrefix]
            private static void Prefix() {
                for (int i = 0; i < Pending.Count; i++) {
                    WaitOut(Pending[i]);
                    Assign(Pending[i]);
                }

                Pending.Clear();
                _forceContext = true;
            }

            [HarmonyFinalizer]
            private static void Finalizer() => _forceContext = false;
        }

        // After every Heightmap.CustomLateUpdate of the frame, so a same-frame re-rebuild has
        // already superseded its entry by the time this looks.
        [HarmonyPatch(typeof(MonoUpdaters), "LateUpdate")]
        internal static class AssignBakedHook {
            [HarmonyPostfix]
            private static void Postfix() {
                for (int i = Pending.Count - 1; i >= 0; i--) {
                    PendingBake bake = Pending[i];
                    if (!bake.m_done) { continue; }

                    Pending.RemoveAt(i);
                    Assign(bake);
                }
            }
        }

        // OnDestroy DestroyImmediates m_collisionMesh, which a worker may be mid-read on.
        [HarmonyPatch(typeof(Heightmap), "OnDestroy")]
        internal static class DestroyGuardHook {
            [HarmonyPrefix]
            private static void Prefix(Heightmap __instance) {
                PendingBake inflight = FindPending(__instance);
                if (inflight == null) { return; }

                WaitOut(inflight);
                Pending.Remove(inflight);
            }
        }
    }
}
