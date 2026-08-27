using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: every building piece subscribes a C# event on its heightmap -
    //
    //   WearNTear.Start:     m_connectedHeightMap.m_clearConnectedWearNTearCache += ClearCachedSupport;
    //   WearNTear.OnDestroy: m_connectedHeightMap.m_clearConnectedWearNTearCache -= ClearCachedSupport;
    //
    // - whose only invoker is Heightmap.Regenerate (Heightmap.cs:230-233), flushing each piece's
    // cached support colliders after a terrain rebuild. A multicast delegate is an immutable
    // array: every += copies it whole, and every -= is an Array.LastIndexOf scan over all
    // subscribers plus another whole copy. With 10-20k pieces on one heightmap that is O(n) per
    // piece and O(n²) for a batch - and crossing a zone boundary loads and unloads pieces in
    // batches. Measured in a 60-90k-instance base: the single worst frame of a crossing (600 ms)
    // was dominated by MulticastDelegate.RemoveImpl, and every operation also allocates a fresh
    // n-element array, feeding the GC pauses seen every ~50 s.
    //
    // Fix: a per-heightmap registry (Dictionary<Heightmap, HashSet<WearNTear>>) instead of the
    // event. Start registers, OnDestroy unregisters - O(1) and allocation-free both ways - and a
    // Regenerate postfix calls ClearCachedSupport on the registered pieces, which is all the event
    // ever did. The event itself is left fully functional for any other subscriber; this mod's
    // pieces simply stop using it.
    //
    // The toggle is safe to flip at runtime in both directions, because the two mechanisms
    // coexist: a piece whose Start ran with the fix off is event-subscribed and serviced by
    // vanilla's invocation; a piece whose Start ran with it on is registry-subscribed and serviced
    // by the postfix. OnDestroy always services both (vanilla's -= runs regardless and is cheap
    // once the event list is small; the registry removal below is unconditional, per the standing
    // rule that index maintenance never sits behind a toggle).
    //
    // Equivalence: registration happens exactly where vanilla subscribed (Start, after the same
    // FindHeightmap call - replicated verbatim, including vanilla's silent acceptance of a piece
    // that finds no heightmap and therefore never gets cache clears), removal exactly where
    // vanilla unsubscribed, and the postfix fires at the same point in Regenerate where vanilla
    // raised the event. ClearCachedSupport only clears three managed lists, so invoking it on a
    // piece already marked for destruction later this frame is as harmless as vanilla's event
    // doing the same. If any maintenance hook failed to attach, Start stands down to vanilla's
    // subscribe for the whole session - a piece must never end up in neither mechanism.
    //
    // Both: a dedicated server runs WearNTear and Heightmap.Regenerate for its active area and
    // pays the same delegate churn.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(WearNTear))]
    internal static class WearCacheEventPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(WearCacheEventPatch),
                ValConfig.SectionPerformance,
                "Fix Piece Event Stall",
                true,
                "Registers building pieces for terrain-change notifications through a lookup " +
                "table instead of a C# event whose subscriber list is copied whole on every " +
                "subscribe and scanned whole on every unsubscribe. In a large base, loading or " +
                "unloading a chunk of pieces through that event is a single multi-hundred-" +
                "millisecond frame.");
        }

        // Keyed by reference (UnityEngine.Object does not override Equals/GetHashCode), so a
        // fake-null heightmap key stays removable until its own OnDestroy drops the whole set.
        private static readonly Dictionary<Heightmap, HashSet<WearNTear>> Registered =
            new Dictionary<Heightmap, HashSet<WearNTear>>();

        private static bool _hooksChecked;
        private static bool _hooksHealthy;

        // Vanilla's Start verbatim (WearNTear.cs:145-151) with the event subscribe replaced by a
        // registry add. FindHeightmap is already O(1) through HeightmapLookupPatch.
        [HarmonyPrefix]
        [HarmonyPatch("Start")]
        private static bool StartPrefix(WearNTear __instance) {
            if (Enabled == null || !Enabled.Value || !HooksHealthy()) { return true; }

            Heightmap hmap = Heightmap.FindHeightmap(__instance.transform.position);
            __instance.m_connectedHeightMap = hmap;
            if (hmap == null) { return false; }

            if (!Registered.TryGetValue(hmap, out HashSet<WearNTear> pieces)) {
                pieces = new HashSet<WearNTear>();
                Registered.Add(hmap, pieces);
            }

            pieces.Add(__instance);
            return false;
        }

        // Unconditional: a piece registered while the toggle was on must leave the registry even
        // if the toggle is off by the time it is destroyed. Vanilla's own -= has already run (this
        // is a postfix) and was a no-op scan for registry-subscribed pieces.
        [HarmonyPostfix]
        [HarmonyPatch("OnDestroy")]
        private static void OnDestroyPostfix(WearNTear __instance) {
            Heightmap hmap = __instance.m_connectedHeightMap;
            if (ReferenceEquals(hmap, null)) { return; }

            if (Registered.TryGetValue(hmap, out HashSet<WearNTear> pieces)) {
                pieces.Remove(__instance);
                if (pieces.Count == 0) { Registered.Remove(hmap); }
            }
        }

        [HarmonyPatch(typeof(Heightmap))]
        internal static class HeightmapHooks {
            // The replacement for vanilla's event invocation, at the same point in Regenerate
            // where the event fires (its last statements).
            [HarmonyPostfix]
            [HarmonyPatch("Regenerate")]
            private static void RegeneratePostfix(Heightmap __instance) {
                if (!Registered.TryGetValue(__instance, out HashSet<WearNTear> pieces)) { return; }

                foreach (WearNTear piece in pieces) { piece.ClearCachedSupport(); }
            }

            // A heightmap unloading takes its whole subscriber set with it, exactly like the
            // event field it replaces; the pieces' own OnDestroy lookups then miss harmlessly.
            [HarmonyPostfix]
            [HarmonyPatch("OnDestroy")]
            private static void OnDestroyPostfix(Heightmap __instance) => Registered.Remove(__instance);
        }

        // A mod suppressing OnDestroy (or a scene teardown race) must not leak the registry
        // across sessions; Shutdown is the session boundary.
        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() => Registered.Clear();
        }

        // ---- hook health ---------------------------------------------------------------------

        /// A registry-subscribed piece is serviced ONLY by the hooks below, so Start must not
        /// route pieces into the registry unless all three attached; otherwise those pieces would
        /// silently stop receiving cache clears after terrain edits.
        private static bool HooksHealthy() {
            if (_hooksChecked) { return _hooksHealthy; }
            _hooksChecked = true;

            _hooksHealthy =
                HasOurPostfix(AccessTools.DeclaredMethod(typeof(WearNTear), "OnDestroy"), typeof(WearCacheEventPatch))
                && HasOurPostfix(AccessTools.DeclaredMethod(typeof(Heightmap), "Regenerate"), typeof(HeightmapHooks))
                && HasOurPostfix(AccessTools.DeclaredMethod(typeof(Heightmap), "OnDestroy"), typeof(HeightmapHooks));

            if (!_hooksHealthy) {
                Logger.LogError(
                    "Piece event fix: a maintenance hook is not attached, so pieces are " +
                    "subscribing through vanilla's event for this session. This usually means a " +
                    "Valheim update changed those methods - look for the patch failure logged at " +
                    "startup.");
            }

            return _hooksHealthy;
        }

        private static bool HasOurPostfix(MethodBase target, System.Type hookClass) {
            // Fully qualified: HarmonyLib.Patches collides with this mod's own Patches namespace.
            HarmonyLib.Patches info = target == null ? null : Harmony.GetPatchInfo(target);
            if (info == null) { return false; }

            foreach (Patch patch in info.Postfixes) {
                if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                if (patch.PatchMethod == null || patch.PatchMethod.DeclaringType != hookClass) { continue; }
                return true;
            }

            return false;
        }
    }
}
