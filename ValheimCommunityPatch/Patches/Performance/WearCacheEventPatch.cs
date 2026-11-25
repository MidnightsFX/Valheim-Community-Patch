using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Piece Event Stall: each building piece joining or leaving its heightmap's cache-clear event copies the list.
    // Prefixes WearNTear.Start to register pieces per heightmap, served by one event subscriber per heightmap.
    // Big bases no longer stall at zone borders on event updates that grew with the square of their piece count.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(WearNTear))]
    internal static class WearCacheEventPatch {
        // The registered pieces of one heightmap, and the one subscriber that serves them. On the event rather
        // than on Regenerate, so a mod that raises it after a deferred rebuild (ValheimOptimized) reaches them too.
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

        // Unconditional: a piece that subscribed to the event instead is not in the registry, and vanilla serves it.
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
