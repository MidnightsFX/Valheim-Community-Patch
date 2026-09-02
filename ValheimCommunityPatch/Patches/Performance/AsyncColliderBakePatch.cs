using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: assigning a MeshCollider's sharedMesh cooks the PhysX collision mesh
    // synchronously on the main thread, and Heightmap.RebuildCollisionMesh does exactly that for
    // every zone heightmap rebuild (Heightmap.cs:505-506). For an already-generated zone entering
    // the active ring - the common case while travelling - that cook is the single largest item in
    // the frame, and it is why crossing a zone boundary stutters even with nothing to generate.
    //
    // Fix: for exactly that case, skip the inline cook, bake the mesh on a worker thread with
    // Physics.BakeMesh (thread-safe off the main thread in this Unity version), and assign
    // sharedMesh at the end of a following frame's LateUpdate - Unity finds the bake already cached
    // for the unmodified mesh, so the assignment is cheap.
    //
    // Two deferral windows, both narrow. Zone spawns with SpawnMode.Client - zones whose content
    // already exists as ZDOs, spawning because they entered the active ring. And delayed-poke
    // rebuilds: TerrainModifier.Awake/OnDestroy call PokeHeightmaps with the delayed flag
    // (TerrainModifier.cs:58,69), which defers the Regenerate to Heightmap.CustomLateUpdate -
    // work that by definition tolerated waiting a frame already, and where, unlike the spawn
    // case, the collider keeps its *previous* mesh during the deferral: stale by centimetres for
    // a frame or two, never absent. Anything urgent (terraforming RPCs, TerrainComp.CheckLoad)
    // uses Poke(false) and never enters that path. Everything else keeps vanilla's synchronous
    // cook, because callers rely on the collider in the same frame:
    //
    //  - Full/Ghost generation raycasts the new terrain for vegetation and location placement in
    //    the same call stack as the Instantiate that triggered the rebuild.
    //  - Terraforming (TerrainComp.RPC_ApplyOperation -> Poke) needs the surface to move underfoot
    //    immediately, and TerrainComp.CheckLoad's load-time rebuild is what everything in an
    //    edited zone snaps against.
    //  - The zone the reference position is in, and any spawn while no local player exists
    //    (initial spawn, respawn, teleport arrival), cook synchronously as well - those are the
    //    paths where something might stand on the new zone this frame.
    //
    // Why the window is safe for the deferred case: the frame before the spawn the zone had no
    // collider at all - it did not exist - and the local player is at least a zone away by the
    // conditions above. A locally-owned item or creature inside the new zone gets its instance from
    // ZNetScene over the following frames anyway; anything already falling there was falling
    // unsupported before the zone spawned. The deferral extends the no-collider state by a frame
    // or two rather than introducing a new one.
    //
    // Two races are handled explicitly, because the worker reads the very mesh a rebuild mutates:
    // a second Regenerate on a map with a bake in flight blocks the few hundred microseconds until
    // the bake call returns, then drops the superseded assignment and proceeds synchronously; and
    // Heightmap.OnDestroy does the same wait, because it DestroyImmediates the collision mesh the
    // worker would otherwise be reading.
    //
    // Client: dedicated servers never take the deferred path - they only spawn Full/Ghost zones and
    // have no local player - so there is nothing for them in the patch at all.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(Heightmap))]
    internal static class AsyncColliderBakePatch {
        private sealed class PendingBake {
            public Heightmap m_hmap;
            public MeshCollider m_collider;
            public Mesh m_mesh;
            public int m_meshId;
            // The collider's own cooking options, captured on the main thread. The sharedMesh
            // assignment only reuses a bake made with *matching* options - the parameterless
            // BakeMesh bakes with Unity's defaults, and the zone collider's options differ, so
            // without this the assignment silently re-cooked on the main thread and the whole
            // deferral bought nothing (measured: the cook simply moved into the assignment hook).
            public MeshColliderCookingOptions m_options;
            public volatile bool m_done;
        }

        // Touched only on the main thread; the worker writes m_done and nothing else.
        private static readonly List<PendingBake> Pending = new List<PendingBake>();
        private static bool _deferContext;
        private static bool _lateUpdateContext;

        private static PendingBake FindPending(Heightmap hmap) {
            for (int i = 0; i < Pending.Count; i++) {
                if (ReferenceEquals(Pending[i].m_hmap, hmap)) { return Pending[i]; }
            }

            return null;
        }

        // The bake call itself is short (a 65x65 grid mesh); this also covers waiting out the
        // ThreadPool picking the item up. Runs at most once per superseding rebuild or destroy,
        // both rare.
        private static void WaitOut(PendingBake bake) {
            while (!bake.m_done) { Thread.Sleep(0); }
        }

        [HarmonyPatch(typeof(ZoneSystem), "SpawnZone")]
        internal static class SpawnZoneContextHook {
            [HarmonyPrefix]
            private static void Prefix(Vector2i zoneID, ZoneSystem.SpawnMode mode) {
                _deferContext =
                    mode == ZoneSystem.SpawnMode.Client
                    && Player.m_localPlayer != null
                    && ZNet.instance != null
                    && zoneID != ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
            }

            // Finalizer rather than postfix so an exception inside SpawnZone cannot leave the
            // context latched for unrelated rebuilds later in the frame.
            [HarmonyFinalizer]
            private static void Finalizer() => _deferContext = false;
        }

        // The delayed-poke window: a Regenerate reached from CustomLateUpdate is a rebuild that
        // TerrainModifier explicitly deferred a frame already. No local player means loading -
        // the initial area builds synchronously, same as the spawn context.
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

            // A rebuild mutates the mesh a queued bake may be reading; settle that first, whatever
            // path this rebuild then takes. The superseded assignment is dropped - this rebuild
            // decides what the collider gets.
            PendingBake inflight = FindPending(__instance);
            if (inflight != null) {
                WaitOut(inflight);
                Pending.Remove(inflight);
            }

            if (!_deferContext && !_lateUpdateContext) { return; }
            if (__instance.m_collider == null) { return; }

            // Vanilla's own `if ((bool) this.m_collider)` around the assignment does the actual
            // skipping; hiding the collider for the duration is what lets the rest of the method -
            // vertices, indices, bounds - run unmodified.
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

        // Assign finished bakes at the end of LateUpdate: after every Heightmap.CustomLateUpdate of
        // the frame (they run inside MonoUpdaters.LateUpdate), so a same-frame re-rebuild has
        // already superseded its entry by the time this looks.
        [HarmonyPatch(typeof(MonoUpdaters), "LateUpdate")]
        internal static class AssignBakedHook {
            [HarmonyPostfix]
            private static void Postfix() {
                for (int i = Pending.Count - 1; i >= 0; i--) {
                    PendingBake bake = Pending[i];
                    if (!bake.m_done) { continue; }

                    Pending.RemoveAt(i);

                    // The zone can be torn down inside the window (UpdateTTL); nothing to assign.
                    if (bake.m_hmap == null || bake.m_collider == null) { continue; }

                    bake.m_collider.sharedMesh = bake.m_mesh;
                }
            }
        }

        // OnDestroy DestroyImmediates m_collisionMesh (Heightmap.cs:146-147) - the mesh a worker
        // may be mid-read on. Settle the bake before vanilla's teardown runs.
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
