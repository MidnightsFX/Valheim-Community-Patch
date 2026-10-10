using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Piece Event Stall: building pieces register for terrain-rebuild cache clears in a
    // per-heightmap table instead of a C# event whose subscribe copies the list and whose
    // unsubscribe scans it.
    //
    // WearNTear.Start subscribes ClearCachedSupport to its heightmap's
    // m_clearConnectedWearNTearCache event and OnDestroy unsubscribes. A multicast delegate is an
    // immutable array, so every += copies it and every -= scans and copies it. With tens of
    // thousands of pieces on one heightmap that is O(n) per piece and O(n^2) for a batch, and
    // crossing a zone boundary loads and unloads pieces in batches, with an allocation each.
    //
    // Start is replaced with a copy that registers the piece in a dictionary (heightmap id ->
    // piece id -> piece) instead of subscribing, and the shared OnDestroy postfix unregisters it.
    // Each heightmap's event gets one subscriber of this fix's own, added with its first piece,
    // which calls ClearCachedSupport on the registered pieces, which is all the event ever did per
    // piece. So whoever raises the event reaches them: vanilla's Regenerate after a full rebuild,
    // and a mod that raises it again once a deferred rebuild has landed (ValheimOptimized does).
    // The event stays functional for any other subscriber. A piece whose Start ran while the hooks
    // were unhealthy is event-subscribed and served by vanilla, so the removal is unconditional.
    //
    // Both: a dedicated server runs WearNTear and Regenerate for its active area.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(WearNTear))]
    internal static class WearCacheEventPatch {
        // The registered pieces of one heightmap, and the one subscriber that serves them.
        private sealed class PieceSet {
            public readonly Dictionary<int, WearNTear> Pieces = new Dictionary<int, WearNTear>();
            public readonly Action Forward;

            public PieceSet() {
                Forward = ClearAll;
            }

            private void ClearAll() {
                foreach (WearNTear piece in Pieces.Values) { piece.ClearCachedSupport(); }
            }
        }

        // Keyed on GetInstanceID() at both levels; see TeardownHooks for the rationale and
        // invariant. A set stays until its heightmap is destroyed, even when it empties: a new set
        // for the same heightmap would subscribe a second time.
        private static readonly Dictionary<int, PieceSet> Registered = new Dictionary<int, PieceSet>();

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(WearCacheEventPatch));

        // A registered piece is served only by these hooks, so Start must not route pieces into
        // the registry unless both attached.
        private static readonly HookHealth Hooks = new HookHealth(
            typeof(WearCacheEventPatch),
            "Piece event fix",
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(WearNTear), "OnDestroy"), typeof(TeardownHooks.PieceHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(Heightmap), "OnDestroy"), typeof(HeightmapHooks)));

        // Vanilla's Start with the event subscribe replaced by a registry add, including its
        // silent acceptance of a piece that finds no heightmap.
        [HarmonyPrefix]
        [HarmonyPatch("Start")]
        private static bool StartPrefix(WearNTear __instance) {
            // Turned off, pieces already registered stay on the one forwarder each heightmap has,
            // which clears them exactly as their own subscriptions would.
            if (ApiSwitch.Off || !Hooks.Healthy) { return true; }

            Heightmap hmap = Heightmap.FindHeightmap(__instance.transform.position);
            __instance.m_connectedHeightMap = hmap;
            if (hmap == null) { return false; }

            int hmapId = hmap.GetInstanceID();
            if (!Registered.TryGetValue(hmapId, out PieceSet set)) {
                set = new PieceSet();
                Registered.Add(hmapId, set);
                hmap.m_clearConnectedWearNTearCache += set.Forward;
            }

            set.Pieces[__instance.GetInstanceID()] = __instance;
            return false;
        }

        /// <summary>The destroy half, called from TeardownHooks' one WearNTear.OnDestroy postfix.</summary>
        internal static void OnPieceDestroyed(WearNTear piece, int pieceId) {
            Heightmap hmap = piece.m_connectedHeightMap;
            if (ReferenceEquals(hmap, null)) { return; }

            if (Registered.TryGetValue(hmap.GetInstanceID(), out PieceSet set)) { set.Pieces.Remove(pieceId); }
        }

        [HarmonyPatch(typeof(Heightmap))]
        internal static class HeightmapHooks {
            // A heightmap unloading takes its whole subscriber set with it, like the event field.
            [HarmonyPostfix]
            [HarmonyPatch("OnDestroy")]
            private static void OnDestroyPostfix(Heightmap __instance) => Registered.Remove(__instance.GetInstanceID());
        }

        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() => Registered.Clear();
        }
    }
}
