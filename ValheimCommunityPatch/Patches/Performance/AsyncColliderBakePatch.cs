using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Zone Collider Stall: RebuildCollisionMesh cooks terrain colliders on the main thread, stuttering zone loads.
    // A prefix and postfix move the cook to Physics.BakeMesh on a worker, and a LateUpdate postfix assigns the result.
    // Already-generated zones entering the active area no longer stall the frame on their collider.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Heightmap))]
    internal static class AsyncColliderBakePatch {
        private const string FixName = "Fix Zone Collider Stall";

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

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(AsyncColliderBakePatch));

        // Asked at the first rebuild this fix would defer, before anything is queued. A mod that can
        // skip or rewrite the rebuild cooks the collider its own way: ValheimOptimized bakes on a Unity
        // job, and both baking at once crashed natively ("pure virtual function call") in BakeMesh.
        private static readonly TakeoverCheck Takeover = new TakeoverCheck(
            typeof(AsyncColliderBakePatch),
            AccessTools.DeclaredMethod(typeof(Heightmap), "RebuildCollisionMesh"),
            transpilers: true,
            owners => $"Terrain collider building is changed by {owners}, so '{FixName}' stands down and " +
                      "that mod's version applies.");

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

            // The worker reads the mesh this rebuild is about to change, so a bake in flight is waited
            // out, then assigned so the collider never falls back to an older shape until the next
            // bake lands. The cook is already cached, so the assignment is cheap.
            PendingBake inflight = FindPending(__instance);
            if (inflight != null) {
                WaitOut(inflight);
                Pending.Remove(inflight);
                Assign(inflight);
            }

            // Only client zone spawns and TerrainModifier's delayed pokes away from the player's zone
            // defer. Fresh generation, terraforming and spawns with no local player cook synchronously:
            // their callers rely on the collider in the same frame.
            if ((!_deferContext && !_lateUpdateContext) || _forceContext) { return; }
            // Turned off, nothing new is deferred; bakes already in flight land as before.
            if (__instance.m_collider == null || ApiSwitch.Off || Takeover.TakenOver) { return; }

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
