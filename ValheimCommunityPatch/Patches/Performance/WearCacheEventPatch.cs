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
    // piece id -> piece) instead of subscribing; the shared OnDestroy postfix unregisters it; and a
    // Heightmap.Regenerate postfix calls ClearCachedSupport on the registered pieces, which is all
    // the event ever did. The event stays functional for any other subscriber. A piece whose Start
    // ran while the hooks were unhealthy is event-subscribed and served by vanilla, so the postfix
    // and the removal are unconditional.
    //
    // Both: a dedicated server runs WearNTear and Regenerate for its active area.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(WearNTear))]
    internal static class WearCacheEventPatch {
        // Both levels keyed on GetInstanceID(); see TeardownHooks for the rationale and invariant.
        private static readonly Dictionary<int, Dictionary<int, WearNTear>> Registered =
            new Dictionary<int, Dictionary<int, WearNTear>>();

        // A registered piece is served only by these hooks, so Start must not route pieces into
        // the registry unless all three attached.
        private static readonly HookHealth Hooks = new HookHealth(
            "Piece event fix",
            () => PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(WearNTear), "OnDestroy"), typeof(TeardownHooks.PieceHook))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(Heightmap), "Regenerate"), typeof(HeightmapHooks))
               && PatchHelper.HasHook(AccessTools.DeclaredMethod(typeof(Heightmap), "OnDestroy"), typeof(HeightmapHooks)));

        // Vanilla's Start with the event subscribe replaced by a registry add, including its
        // silent acceptance of a piece that finds no heightmap.
        [HarmonyPrefix]
        [HarmonyPatch("Start")]
        private static bool StartPrefix(WearNTear __instance) {
            if (!Hooks.Healthy) { return true; }

            Heightmap hmap = Heightmap.FindHeightmap(__instance.transform.position);
            __instance.m_connectedHeightMap = hmap;
            if (hmap == null) { return false; }

            int hmapId = hmap.GetInstanceID();
            if (!Registered.TryGetValue(hmapId, out Dictionary<int, WearNTear> pieces)) {
                pieces = new Dictionary<int, WearNTear>();
                Registered.Add(hmapId, pieces);
            }

            pieces[__instance.GetInstanceID()] = __instance;
            return false;
        }

        /// <summary>The destroy half, called from TeardownHooks' one WearNTear.OnDestroy postfix.</summary>
        internal static void OnPieceDestroyed(WearNTear piece, int pieceId) {
            Heightmap hmap = piece.m_connectedHeightMap;
            if (ReferenceEquals(hmap, null)) { return; }

            int hmapId = hmap.GetInstanceID();
            if (Registered.TryGetValue(hmapId, out Dictionary<int, WearNTear> pieces)) {
                pieces.Remove(pieceId);
                if (pieces.Count == 0) { Registered.Remove(hmapId); }
            }
        }

        [HarmonyPatch(typeof(Heightmap))]
        internal static class HeightmapHooks {
            // The same point in Regenerate where vanilla raises the event.
            [HarmonyPostfix]
            [HarmonyPatch("Regenerate")]
            private static void RegeneratePostfix(Heightmap __instance) {
                if (!Registered.TryGetValue(__instance.GetInstanceID(), out Dictionary<int, WearNTear> pieces)) { return; }

                foreach (WearNTear piece in pieces.Values) { piece.ClearCachedSupport(); }
            }

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
