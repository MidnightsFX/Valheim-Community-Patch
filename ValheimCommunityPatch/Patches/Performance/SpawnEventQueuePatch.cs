using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: ZNetScene.CreateDestroyObjects rediscovers its own candidate set thirty
    // times a second. Every pass clears both temp lists and calls ZDOMan.FindSectorObjects, which
    // walks every sector in the streamed ring and copies every ZDO out of the sector stores
    // (ZDOMan.cs:693-728); CreateObjectsSorted then re-filters that whole list on Created and
    // re-sorts it by distance (ZNetScene.cs:152-189), only to consume its head. None of that
    // depends on anything having changed - the ring's contents are the same set they were 33 ms
    // ago, minus what was just spawned.
    //
    // Measured in a crossing-heavy session, per second of frame time, spiky seconds vs calm:
    // the ring scan 24.3 / 13.7, the sort 28.3 / 20.0, the re-filter 9.1 / 7.1 - against 36.5 /
    // 30.4 for the Instantiate calls that are the actual work. Worst single seconds put the sort
    // at 214 ms and the scan at 200 ms. That is the zone-boundary hitch: not the spawning, but
    // the pipeline re-deriving what to spawn while the candidate set is at its largest.
    //
    // Fix: hold the candidate set as a persistent QUEUE of exactly the in-ring, uncreated ZDOs,
    // maintained by the events that can change it, and make the per-pass work "consume the head".
    // FindSectorObjects is never called. Four feeds, three of them on hooks this mod already
    // owns for other indexes:
    //
    //  1. Zone-set diff. Persistent near and distant zone sets; when the reference zone moves -
    //     or either ring radius changes, which is how a radius edit from another mod self-heals,
    //     the same snapshot-compare SceneIdleSkipPatch uses - entering zones enqueue their sector
    //     store's contents and departing zones dequeue theirs. A crossing therefore enqueues one
    //     new zone column as an event instead of rescanning the world.
    //  2. ZDOMan.AddToSector / RemoveFromSector. Verified elsewhere in this mod as the only
    //     mutators of both sector stores (including the outside-sector map): every creation on
    //     every path, every destroy, every sector crossing and InvalidateSector pass through
    //     them. A ZDO arriving in an in-ring sector uncreated enqueues; leaving dequeues.
    //  3. ZDOMan.RPC_ZDOData. Enqueues raised while that handler runs are BUFFERED and flushed in
    //     its postfix, so a ZDO caught mid-deserialisation - the handler bounces every incoming
    //     ZDO out to a sentinel sector and back (ZDOMan.cs:634) - never enters the queue on the
    //     strength of a half-written sector. Not speculative: the idle-skip's ring hash hit this
    //     exact hazard, and ontrigger's implementation hit it independently.
    //  4. ZDO.set_Created. True dequeues. False on an in-ring ZDO RE-enqueues, which preserves
    //     vanilla's recreate-what-something-destroyed behaviour: ZNetScene.Destroy resets a
    //     non-owned ZDO and vanilla respawns it on the next pass.
    //
    // Consumption keeps vanilla's shape: the same budget formula against the queue's length, the
    // same nearest-first order, the same IsActiveAreaLoaded gate, the same server-side
    // invalid-prefab branch. What changes is that the sort is LAZY - re-run only when the queue
    // is dirty or the player has moved 8 m, the distance at which the ordering meaningfully
    // changes - and that consumption tombstones slots rather than reindexing, so a pass costs
    // what it creates rather than what is waiting.
    //
    // Two degeneration guards, both learned the hard way here:
    //  - Entries whose zone is not ready yet are moved to a DEFERRED list and spliced back every
    //    few passes, instead of being re-tested every pass. Vanilla re-tests all of them thirty
    //    times a second; while a region generates, that is the whole queue. The retry latency
    //    this trades is the ~100 ms SpawnQueueCachePatch already established as invisible at
    //    zone-generation granularity. A client whose CreateObject returns null (an orphaned
    //    modded ZDO with no prefab) defers on the same list rather than being walked forever.
    //  - Removal is O(1). A mid-queue removal that reindexes the tail is O(n) per entry and
    //    O(n^2) across a fully gated queue, which is precisely the regime the deferred list
    //    exists for.
    //
    // The queue is keyed by ZDOID, not by ZDO. ZDO overrides GetHashCode to hash its m_uid but
    // does not override Equals (ZDO.cs:46), so a ZDO used as a dictionary key whose uid changes
    // under the ZDO pool lands in the wrong bucket and becomes unreachable. ZDOID is a value key
    // and cannot rot that way; the parallel id list then catches a pooled recycle at consume
    // time, exactly as SpawnQueueCachePatch's guard does, before a recycled slot can be
    // instantiated off-ring or fed to the server-side destroy branch.
    //
    // HARD PRECONDITION - the reason this class checks more than its own toggle. Replacing the
    // pass means m_tempCurrentObjects and m_tempCurrentDistantObjects stay EMPTY, and
    // ZNetScene.RemoveObjects treats those two lists as its keep-set: vanilla's earmark discovery
    // handed two empty lists would unload every object in the world. ZoneDiffRemovalPatch ignores
    // both arguments and answers from the per-zone instance index, which is why the two compose -
    // so this class stands down entirely unless that fix is on and its index is healthy.
    //
    // "Verify Spawn Queue" runs vanilla's FindSectorObjects and filter on every pass, compares
    // the candidate set against the queue, ACTS ON VANILLA'S, and reports engagement plus any
    // divergence with its reason. This has the largest hook-enumeration surface of anything in
    // this mod and the wake-signal history says the first verify session finds a missed feed.
    //
    // Both: a dedicated server streams objects for connected players through this same pass.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ZNetScene))]
    internal static class SpawnEventQueuePatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(SpawnEventQueuePatch),
                ValConfig.SectionPerformance,
                "Fix Object Stream Rescan",
                true,
                "Keeps the set of objects waiting to stream in as a running list updated by the " +
                "events that change it, instead of rescanning every loaded zone thirty times a " +
                "second to rediscover it. Crossing a zone border adds one new column of zones " +
                "rather than re-reading the whole loaded area, which is where the border stutter " +
                "comes from. Objects still spawn at the same rate, nearest first.");

            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Spawn Queue",
                false,
                "Diagnostic. Rebuilds the candidate list the vanilla way on every pass, compares " +
                "it against the maintained queue, acts on vanilla's answer, and logs anything " +
                "the queue is missing. Costs the whole scan this fix exists to avoid, so leave " +
                "it off unless you are validating the queue.",
                advanced: true);
        }

        // The distance at which a nearest-first ordering has meaningfully changed. Below it a
        // re-sort would reproduce almost the same order for the cost of the whole sort.
        private const float ResortDistanceSqr = 8f * 8f;

        // How many passes a not-ready or unresolvable entry waits before being retried. At 30 Hz
        // this is the same ~100 ms staleness SpawnQueueCachePatch's design is written against.
        private const int DeferredRecheckPasses = 3;

        // Compact once tombstones are a quarter of the backing list.
        private const int CompactTombstoneShare = 4;

        // A re-sort is also earned by the unsorted tail growing past an eighth of the queue, or
        // past this many entries outright while the queue is small. Without a threshold like
        // this the queue re-sorts on every pass that anything arrives, which is every pass while
        // an area streams - exactly when the sort is most expensive.
        private const int AppendResortShare = 8;
        private const int MinAppendsBeforeResort = 32;

        /// One side of the stream (near or distant). Entries are consumed in order from m_head;
        /// a consumed or invalidated slot becomes a tombstone rather than being spliced out, and
        /// the list is compacted in one sweep when the tombstones earn it or a re-sort needs it.
        private sealed class SpawnQueue {
            internal readonly List<ZDO> m_entries = new List<ZDO>();
            internal readonly List<ZDOID> m_ids = new List<ZDOID>();

            // ZDOID -> slot, or DeferredSlot while the entry sits on m_deferred. Membership is
            // the whole map's key set, so a feed can never double-enqueue a deferred entry.
            internal readonly Dictionary<ZDOID, int> m_index = new Dictionary<ZDOID, int>();
            internal readonly List<ZDO> m_deferred = new List<ZDO>();

            internal int m_head;
            internal int m_tombstones;
            internal bool m_sortDirty = true;
            internal int m_appendedSinceSort;
            internal int m_passesSinceSplice;

            internal const int DeferredSlot = -1;

            /// Everything still waiting, deferred included - vanilla's budget divides the whole
            /// uncreated backlog, not just the part it can act on this pass.
            internal int Pending => m_index.Count;

            internal void Enqueue(ZDO zdo) {
                ZDOID id = zdo.m_uid;
                if (m_index.ContainsKey(id)) { return; }

                m_index[id] = m_entries.Count;
                m_entries.Add(zdo);
                m_ids.Add(id);

                // Deliberately NOT m_sortDirty. Arrivals are continuous while anything streams,
                // so dirtying on each one re-sorts every pass and gives back the whole saving.
                // An append lands at the tail, which only costs ordering, so the threshold in
                // PrepareForConsume decides when enough of them have piled up to matter.
                m_appendedSinceSort++;
            }

            internal void Dequeue(ZDOID id) {
                if (!m_index.TryGetValue(id, out int slot)) { return; }

                m_index.Remove(id);
                if (slot == DeferredSlot) {
                    for (int i = 0; i < m_deferred.Count; i++) {
                        if (m_deferred[i].m_uid != id) { continue; }
                        m_deferred[i] = m_deferred[m_deferred.Count - 1];
                        m_deferred.RemoveAt(m_deferred.Count - 1);
                        break;
                    }

                    return;
                }

                m_entries[slot] = null;
                m_tombstones++;
            }

            /// Consumed or invalidated. Idempotent, because CreateObject sets Created on the
            /// ZDO it spawns and that runs back through the feed below, retiring this very slot
            /// before the consume loop gets to it.
            internal void Tombstone(int slot) {
                if (m_entries[slot] == null) { return; }

                m_index.Remove(m_ids[slot]);
                m_entries[slot] = null;
                m_tombstones++;
            }

            internal void Defer(int slot) {
                ZDO zdo = m_entries[slot];
                if (zdo == null) { return; }

                m_deferred.Add(zdo);
                m_index[m_ids[slot]] = DeferredSlot;
                m_entries[slot] = null;
                m_tombstones++;
            }

            /// Deferred entries rejoin the tail; the sort that follows puts them back in order.
            internal void SpliceDeferred() {
                if (m_deferred.Count == 0) { return; }

                for (int i = 0; i < m_deferred.Count; i++) {
                    ZDO zdo = m_deferred[i];
                    m_index[zdo.m_uid] = m_entries.Count;
                    m_entries.Add(zdo);
                    m_ids.Add(zdo.m_uid);
                }

                m_appendedSinceSort += m_deferred.Count;
                m_deferred.Clear();
            }

            /// Drops tombstones and rebuilds both the id list and the index from the survivors.
            /// The index rebuild is also the hygiene net for any key a recycled ZDO stranded.
            internal void Compact() {
                int write = 0;
                for (int read = 0; read < m_entries.Count; read++) {
                    ZDO zdo = m_entries[read];
                    if (zdo == null) { continue; }

                    m_entries[write] = zdo;
                    m_ids[write] = m_ids[read];
                    write++;
                }

                m_entries.RemoveRange(write, m_entries.Count - write);
                m_ids.RemoveRange(write, m_ids.Count - write);
                m_tombstones = 0;
                m_head = 0;
                ReindexFromEntries();
            }

            internal void ReindexFromEntries() {
                // Deferred membership is re-stamped after the entries so it survives the rebuild.
                m_index.Clear();
                for (int i = 0; i < m_entries.Count; i++) { m_index[m_ids[i]] = i; }
                for (int i = 0; i < m_deferred.Count; i++) { m_index[m_deferred[i].m_uid] = DeferredSlot; }
            }

            internal void Clear() {
                m_entries.Clear();
                m_ids.Clear();
                m_index.Clear();
                m_deferred.Clear();
                m_head = 0;
                m_tombstones = 0;
                m_sortDirty = true;
                m_appendedSinceSort = 0;
                m_passesSinceSplice = 0;
            }
        }

        private static readonly SpawnQueue Near = new SpawnQueue();
        private static readonly SpawnQueue Distant = new SpawnQueue();

        private static readonly HashSet<Vector2i> NearZones = new HashSet<Vector2i>();
        private static readonly HashSet<Vector2i> DistantZones = new HashSet<Vector2i>();
        private static readonly HashSet<Vector2i> ScratchNearZones = new HashSet<Vector2i>();
        private static readonly HashSet<Vector2i> ScratchDistantZones = new HashSet<Vector2i>();
        private static readonly List<ZDO> ScratchSector = new List<ZDO>();

        private static Vector2i _snapshotZone = new Vector2i(int.MinValue, int.MinValue);
        private static int _snapshotArea = -1;
        private static int _snapshotDistantArea = -1;
        private static ZNetScene _snapshotScene;

        private static Vector3 _lastSortPosition;
        private static bool _sortPositionValid;

        // RPC_ZDOData buffering (feed 3).
        private static bool _inZdoData;
        private static readonly List<ZDO> PendingRpc = new List<ZDO>();

        private static bool _hooksChecked;
        private static bool _hooksHealthy;

        // ---- the pass ----------------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch("CreateDestroyObjects")]
        private static bool CreateDestroyObjectsPrefix(ZNetScene __instance) {
            // The ring snapshot is maintained unconditionally, like every other index in this
            // mod: the feeds below decide in-ring membership from it, so it has to be current
            // the moment the fix is switched on - not merely from then onwards.
            if (ZNet.instance != null && ZoneSystem.instance != null && ZDOMan.instance != null) {
                // A new scene is a new world: the queue indexes into ZDOs the old session owned.
                if (!ReferenceEquals(__instance, _snapshotScene)) { ResetSession(__instance); }

                ZoneSystem zoneSystem = ZoneSystem.instance;
                Vector2i zone = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
                SyncZoneSets(zone, zoneSystem.m_activeArea, zoneSystem.m_activeDistantArea);

                if (Enabled != null && Enabled.Value && Verify != null && Verify.Value) {
                    RunVerify(zone, zoneSystem);
                    return true;
                }
            }

            FinishVerify();
            if (!Driving()) { return true; }

            // Vanilla's own list objects, left empty on purpose: ZoneDiffRemovalPatch ignores
            // them (Driving() guarantees it is the one answering RemoveObjects).
            __instance.m_tempCurrentObjects.Clear();
            __instance.m_tempCurrentDistantObjects.Clear();

            __instance.CreateObjects(__instance.m_tempCurrentObjects, __instance.m_tempCurrentDistantObjects);
            __instance.RemoveObjects(__instance.m_tempCurrentObjects, __instance.m_tempCurrentDistantObjects);
            return false;
        }

        // Consumption replaces both creators. Priority.First so this sorts ahead of
        // SpawnQueueCachePatch's prefix on the same method, which must not run while the queue is
        // driving - returning false here skips it, and it resumes untouched when this is off.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch("CreateObjectsSorted")]
        private static bool CreateObjectsSortedPrefix(
            ZNetScene __instance, int maxCreatedPerFrame, ref int created) {
            if (!Driving()) { return true; }
            if (!ZoneSystem.instance.IsActiveAreaLoaded()) { return false; }

            Vector3 referencePosition = ZNet.instance.GetReferencePosition();
            PrepareForConsume(Near, referencePosition, true);

            int budget = Mathf.Max(
                Near.Pending / (SpawnQueueCachePatch.BurstDivisor != null
                    ? SpawnQueueCachePatch.BurstDivisor.Value
                    : 100),
                maxCreatedPerFrame);

            AdvanceHead(Near);
            for (int i = Near.m_head; i < Near.m_entries.Count; i++) {
                ZDO zdo = Near.m_entries[i];
                if (zdo == null) { continue; }

                // A pooled ZDO recycled into a different object keeps the reference but changes
                // its uid; instantiating that slot would spawn something off-ring, or feed a live
                // ZDO to the server-side destroy branch below.
                if (zdo.m_uid != Near.m_ids[i] || zdo.Created) {
                    Near.Tombstone(i);
                    continue;
                }

                if (!ZoneSystem.instance.IsZoneReadyForType(zdo.GetSector(), zdo.Type)) {
                    Near.Defer(i);
                    continue;
                }

                if (__instance.CreateObject(zdo) != null) {
                    Near.Tombstone(i);
                    ++created;
                    if (created > budget) { break; }
                } else if (ZNet.instance.IsServer()) {
                    // Vanilla's branch verbatim. The destroy takes the ZDO out of its sector,
                    // which dequeues it through feed 2.
                    zdo.SetOwner(ZDOMan.GetSessionID());
                    ZLog.Log("Destroyed invalid predab ZDO:" + zdo.m_uid);
                    ZDOMan.instance.DestroyZDO(zdo);
                } else {
                    // No prefab resolves on this client and nothing will change that soon; park
                    // it rather than walking it thirty times a second forever.
                    Near.Defer(i);
                }
            }

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch("CreateDistantObjects")]
        private static bool CreateDistantObjectsPrefix(
            ZNetScene __instance, int maxCreatedPerFrame, ref int created) {
            if (!Driving()) { return true; }

            // Before the budget early-out, so the deferred splice keeps its cadence on the passes
            // the near path saturates. No sort: vanilla's distant path consumes in arrival order.
            PrepareForConsume(Distant, Vector3.zero, false);
            if (created > maxCreatedPerFrame) { return false; }

            AdvanceHead(Distant);
            for (int i = Distant.m_head; i < Distant.m_entries.Count; i++) {
                ZDO zdo = Distant.m_entries[i];
                if (zdo == null) { continue; }

                if (zdo.m_uid != Distant.m_ids[i] || zdo.Created) {
                    Distant.Tombstone(i);
                    continue;
                }

                if (__instance.CreateObject(zdo) != null) {
                    Distant.Tombstone(i);
                    ++created;
                    if (created > maxCreatedPerFrame) { break; }
                } else if (ZNet.instance.IsServer()) {
                    zdo.SetOwner(ZDOMan.GetSessionID());
                    ZLog.Log($"Destroyed invalid predab ZDO:{zdo.m_uid}  prefab hash:{zdo.GetPrefab()}");
                    ZDOMan.instance.DestroyZDO(zdo);
                } else {
                    Distant.Defer(i);
                }
            }

            return false;
        }

        private static void AdvanceHead(SpawnQueue queue) {
            while (queue.m_head < queue.m_entries.Count && queue.m_entries[queue.m_head] == null) {
                queue.m_head++;
            }
        }

        private static void PrepareForConsume(SpawnQueue queue, Vector3 referencePosition, bool sorted) {
            if (++queue.m_passesSinceSplice >= DeferredRecheckPasses) {
                queue.m_passesSinceSplice = 0;
                queue.SpliceDeferred();
            }

            bool moved = sorted
                && (!_sortPositionValid
                    || Utils.DistanceSqr(_lastSortPosition, referencePosition) >= ResortDistanceSqr);

            if (!sorted) {
                // Nothing reorders the distant queue, so it only ever needs compaction.
                if (queue.m_tombstones > queue.m_entries.Count / CompactTombstoneShare) { queue.Compact(); }
                return;
            }

            // Enough entries have piled up at the unsorted tail to be worth folding back in.
            bool tailGrew = queue.m_appendedSinceSort
                > Mathf.Max(MinAppendsBeforeResort, queue.m_entries.Count / AppendResortShare);

            if (!queue.m_sortDirty && !moved && !tailGrew) {
                if (queue.m_tombstones > queue.m_entries.Count / CompactTombstoneShare) { queue.Compact(); }
                return;
            }

            queue.Compact();

            for (int i = 0; i < queue.m_entries.Count; i++) {
                ZDO zdo = queue.m_entries[i];
                zdo.m_tempSortValue = Utils.DistanceSqr(referencePosition, zdo.GetPosition());
            }

            // Vanilla's comparator, so the spawn order is vanilla's spawn order.
            queue.m_entries.Sort(ZNetScene.ZDOCompare);

            queue.m_ids.Clear();
            for (int i = 0; i < queue.m_entries.Count; i++) { queue.m_ids.Add(queue.m_entries[i].m_uid); }
            queue.ReindexFromEntries();

            queue.m_sortDirty = false;
            queue.m_appendedSinceSort = 0;
            _lastSortPosition = referencePosition;
            _sortPositionValid = true;
        }

        // ---- feed 1: the zone-set diff -------------------------------------------------------

        private static void SyncZoneSets(Vector2i zone, int area, int distantArea) {
            if (zone == _snapshotZone && area == _snapshotArea && distantArea == _snapshotDistantArea) {
                return;
            }

            _snapshotZone = zone;
            _snapshotArea = area;
            _snapshotDistantArea = distantArea;

            BuildZoneSets(zone, area, distantArea, ScratchNearZones, ScratchDistantZones);

            foreach (Vector2i entering in ScratchNearZones) {
                if (NearZones.Contains(entering)) { continue; }
                EnqueueSector(entering, Near, false);
            }

            foreach (Vector2i leaving in NearZones) {
                if (ScratchNearZones.Contains(leaving)) { continue; }
                DequeueSector(leaving, Near);
            }

            foreach (Vector2i entering in ScratchDistantZones) {
                if (DistantZones.Contains(entering)) { continue; }
                EnqueueSector(entering, Distant, true);
            }

            foreach (Vector2i leaving in DistantZones) {
                if (ScratchDistantZones.Contains(leaving)) { continue; }
                DequeueSector(leaving, Distant);
            }

            NearZones.Clear();
            foreach (Vector2i z in ScratchNearZones) { NearZones.Add(z); }
            DistantZones.Clear();
            foreach (Vector2i z in ScratchDistantZones) { DistantZones.Add(z); }
        }

        /// The two rings FindSectorObjects walks (ZDOMan.cs:693-728): the near set is the
        /// Chebyshev square of radius area and takes ALL its ZDOs - no Distant filter, which is
        /// where ontrigger's version diverges - and the distant band is the shell from area+1 to
        /// area+distantArea, taking Distant-flagged ZDOs only.
        private static void BuildZoneSets(
            Vector2i center, int area, int distantArea,
            HashSet<Vector2i> nearZones, HashSet<Vector2i> distantZones) {
            nearZones.Clear();
            distantZones.Clear();

            for (int x = center.x - area; x <= center.x + area; x++) {
                for (int y = center.y - area; y <= center.y + area; y++) {
                    nearZones.Add(new Vector2i(x, y));
                }
            }

            int full = area + distantArea;
            for (int x = center.x - full; x <= center.x + full; x++) {
                for (int y = center.y - full; y <= center.y + full; y++) {
                    int dx = x - center.x;
                    int dy = y - center.y;
                    if (dx < 0) { dx = -dx; }
                    if (dy < 0) { dy = -dy; }
                    if ((dx > dy ? dx : dy) <= area) { continue; }
                    distantZones.Add(new Vector2i(x, y));
                }
            }
        }

        private static void EnqueueSector(Vector2i sector, SpawnQueue queue, bool distantOnly) {
            ScratchSector.Clear();
            if (distantOnly) {
                ZDOMan.instance.FindDistantObjects(sector, ScratchSector);
            } else {
                ZDOMan.instance.FindObjects(sector, ScratchSector);
            }

            for (int i = 0; i < ScratchSector.Count; i++) {
                ZDO zdo = ScratchSector[i];
                if (zdo.Created) { continue; }
                queue.Enqueue(zdo);
            }

            ScratchSector.Clear();
        }

        private static void DequeueSector(Vector2i sector, SpawnQueue queue) {
            ScratchSector.Clear();
            ZDOMan.instance.FindObjects(sector, ScratchSector);
            for (int i = 0; i < ScratchSector.Count; i++) { queue.Dequeue(ScratchSector[i].m_uid); }
            ScratchSector.Clear();
        }

        // ---- feeds 2-4: the event hooks ------------------------------------------------------
        //
        // Maintenance, never behind the toggle: the queue must be correct the moment the fix is
        // switched on, and an unmaintained queue that is switched on later would silently miss
        // everything that happened while it was off.

        [HarmonyPatch(typeof(ZDOMan), "AddToSector")]
        internal static class AddToSectorHook {
            [HarmonyPostfix]
            private static void Postfix(ZDO zdo, Vector2i sector) {
                if (zdo.Created) { return; }

                if (_inZdoData) {
                    // Deferred to the handler's postfix: mid-handler the ZDO may be halfway
                    // through deserialisation and its sector is bounced out and back.
                    PendingRpc.Add(zdo);
                    return;
                }

                EnqueueIfInRing(zdo, sector);
            }
        }

        [HarmonyPatch(typeof(ZDOMan), "RemoveFromSector")]
        internal static class RemoveFromSectorHook {
            [HarmonyPostfix]
            private static void Postfix(ZDO zdo) {
                Near.Dequeue(zdo.m_uid);
                Distant.Dequeue(zdo.m_uid);
            }
        }

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
        internal static class ZdoDataHook {
            [HarmonyPrefix]
            private static void Prefix() {
                _inZdoData = true;
                PendingRpc.Clear();
            }

            [HarmonyPostfix]
            private static void Postfix() {
                _inZdoData = false;
                for (int i = 0; i < PendingRpc.Count; i++) {
                    ZDO zdo = PendingRpc[i];
                    if (zdo.Created) { continue; }
                    EnqueueIfInRing(zdo, zdo.GetSector());
                }

                PendingRpc.Clear();
            }
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Created), MethodType.Setter)]
        internal static class CreatedSetterHook {
            [HarmonyPostfix]
            private static void Postfix(ZDO __instance) {
                if (__instance.Created) {
                    Near.Dequeue(__instance.m_uid);
                    Distant.Dequeue(__instance.m_uid);
                    return;
                }

                // Cleared again: something destroyed the object while the ZDO stayed in the ring,
                // and vanilla would recreate it on the next pass.
                EnqueueIfInRing(__instance, __instance.GetSector());
            }
        }

        private static void EnqueueIfInRing(ZDO zdo, Vector2i sector) {
            if (NearZones.Contains(sector)) {
                Near.Enqueue(zdo);
                return;
            }

            if (zdo.Distant && DistantZones.Contains(sector)) { Distant.Enqueue(zdo); }
        }

        // ---- engagement ----------------------------------------------------------------------

        /// True when the queue is actually replacing the pass. Verify deliberately is not: it
        /// runs vanilla and acts on vanilla's answer, comparing the queue against it.
        private static bool Driving() => (Verify == null || !Verify.Value) && Engaged();

        /// Every condition that must hold before the queue may replace the pass. The removal
        /// interlock is the load-bearing one: see the header.
        private static bool Engaged() {
            if (Enabled == null || !Enabled.Value) { return false; }
            if (ZNet.instance == null || ZoneSystem.instance == null || ZDOMan.instance == null) { return false; }
            if (!HooksHealthy()) { return false; }

            if (ZoneDiffRemovalPatch.Enabled == null || !ZoneDiffRemovalPatch.Enabled.Value) { return false; }
            if (!SectorInstanceIndexPatch.MaintenanceHealthy()) { return false; }

            // That fix's own verify reconstructs vanilla's keep-set from the two lists this
            // class leaves empty, so the two diagnostics do not compose - stand aside and let it
            // measure against the vanilla pass it is written against.
            if (ZoneDiffRemovalPatch.Verify != null && ZoneDiffRemovalPatch.Verify.Value) { return false; }

            return true;
        }

        private static void ResetSession(ZNetScene scene) {
            Near.Clear();
            Distant.Clear();
            NearZones.Clear();
            DistantZones.Clear();
            PendingRpc.Clear();
            _inZdoData = false;
            _sortPositionValid = false;
            _snapshotZone = new Vector2i(int.MinValue, int.MinValue);
            _snapshotArea = -1;
            _snapshotDistantArea = -1;
            _snapshotScene = scene;
        }

        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        internal static class ShutdownHook {
            [HarmonyPostfix]
            private static void Postfix() => ResetSession(null);
        }

        /// The queue is only correct if every feed is attached; any missing one and this class
        /// stands down to the idle-skip and spawn-cache stack it sits above.
        private static bool HooksHealthy() {
            if (_hooksChecked) { return _hooksHealthy; }
            _hooksChecked = true;

            _hooksHealthy =
                HasOurHook(AccessTools.DeclaredMethod(typeof(ZDOMan), "AddToSector"), typeof(AddToSectorHook))
                && HasOurHook(AccessTools.DeclaredMethod(typeof(ZDOMan), "RemoveFromSector"), typeof(RemoveFromSectorHook))
                && HasOurHook(AccessTools.DeclaredMethod(typeof(ZDOMan), "RPC_ZDOData"), typeof(ZdoDataHook))
                && HasOurHook(AccessTools.PropertySetter(typeof(ZDO), "Created"), typeof(CreatedSetterHook))
                && HasOurHook(AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObjectsSorted"), typeof(SpawnEventQueuePatch))
                && HasOurHook(AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateDistantObjects"), typeof(SpawnEventQueuePatch));

            if (!_hooksHealthy) {
                Logger.LogError(
                    "Spawn event queue: a feed hook is not attached, so the queue cannot be kept " +
                    "correct and object streaming is running the previous path for this session. " +
                    "This usually means a Valheim update changed those methods - look for the " +
                    "patch failure logged at startup.");
            }

            return _hooksHealthy;
        }

        private static bool HasOurHook(MethodBase target, System.Type hookClass) {
            HarmonyLib.Patches info = target == null ? null : Harmony.GetPatchInfo(target);
            if (info == null) { return false; }

            foreach (Patch patch in info.Postfixes) {
                if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                if (patch.PatchMethod == null || patch.PatchMethod.DeclaringType != hookClass) { continue; }
                return true;
            }

            foreach (Patch patch in info.Prefixes) {
                if (patch.owner != ValheimCommunityPatch.PluginGUID) { continue; }
                if (patch.PatchMethod == null || patch.PatchMethod.DeclaringType != hookClass) { continue; }
                return true;
            }

            return false;
        }

        // ---- verify ---------------------------------------------------------------------------

        private const int VerifyReportInterval = 900;

        private static readonly List<ZDO> VerifyNear = new List<ZDO>();
        private static readonly List<ZDO> VerifyDistant = new List<ZDO>();

        private static bool _verifyActive;
        private static long _verifyPasses;
        private static long _verifyQueued;
        private static long _verifyExpected;
        private static long _verifyMissing;
        private static int _passesSinceReport;

        /// Builds the candidate set the vanilla way and compares it against the queue. Anything
        /// vanilla would spawn that the queue does not hold is a missed feed - an object that
        /// would simply never appear - so that is what this counts and names.
        private static void RunVerify(Vector2i zone, ZoneSystem zoneSystem) {
            _verifyActive = true;
            _verifyPasses++;

            VerifyNear.Clear();
            VerifyDistant.Clear();
            ZDOMan.instance.FindSectorObjects(
                zone, zoneSystem.m_activeArea, zoneSystem.m_activeDistantArea, VerifyNear, VerifyDistant);

            _verifyQueued += Near.Pending + Distant.Pending;

            CompareSide(VerifyNear, Near, "near");
            CompareSide(VerifyDistant, Distant, "distant");

            if (++_passesSinceReport >= VerifyReportInterval) {
                _passesSinceReport = 0;
                LogVerifySummary("periodic");
            }
        }

        private static void CompareSide(List<ZDO> vanilla, SpawnQueue queue, string side) {
            for (int i = 0; i < vanilla.Count; i++) {
                ZDO zdo = vanilla[i];
                if (zdo.Created) { continue; }

                _verifyExpected++;
                if (queue.m_index.ContainsKey(zdo.m_uid)) { continue; }

                _verifyMissing++;
                Logger.LogError(
                    $"Spawn queue verify: MISSING from the {side} queue - ZDO {zdo.m_uid} " +
                    $"(prefab {zdo.GetPrefab()}, sector {zdo.m_sector}) is an uncreated candidate " +
                    "vanilla would spawn. A feed is missing. Please report this - leave 'Fix " +
                    "Object Stream Rescan' off until it is understood.");
            }
        }

        private static void FinishVerify() {
            if (!_verifyActive) { return; }
            _verifyActive = false;
            LogVerifySummary("final");
            _verifyPasses = 0;
            _verifyQueued = 0;
            _verifyExpected = 0;
            _verifyMissing = 0;
            _passesSinceReport = 0;
        }

        private static void LogVerifySummary(string kind) {
            Logger.LogInfo(
                $"Spawn queue verify ({kind}): {_verifyPasses} pass(es), " +
                $"{_verifyExpected} vanilla candidate(s) seen against " +
                $"{_verifyQueued} queued, {_verifyMissing} missing.");
        }
    }
}
