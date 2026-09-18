using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Evict Dead ZDO Records: the server drops its record of a destroyed object once it is old
    // enough that no stale copy of it can still be on the wire.
    //
    // ZDOMan.m_deadZDOs records the id of every ZDO destroyed on the server, against a game-time
    // stamp nothing reads. Its one use is in RPC_ZDOData: a create that arrives for a dead id is
    // destroyed again, so a client whose update for an object crossed the destroy in flight cannot
    // resurrect it. The dictionary is cleared only when a world loads, so every arrow, dropped
    // item, felled tree and killed creature adds an entry for the life of the process. The window
    // it guards is the in-flight time of that client's queued changes: a client drops its own copy
    // as soon as the routed destroy reaches it and sends nothing more for it.
    //
    // A postfix on ZDOMan.HandleDestroyedZDO queues the id with the real time it died; a postfix
    // on ZDOMan.Update dequeues records older than 'Dead ZDO Retain Seconds' and removes the
    // entry, provided it still carries the stamp recorded here, so an id destroyed twice keeps its
    // newer record. Real time rather than the stored stamp, because game time jumps forward when
    // players sleep. Entries made while the fix was off are left as vanilla leaves them.
    //
    // Server: vanilla records dead ids only on the host. Off by default.
    [PatchSide(Side.Server)]
    [HarmonyPatch(typeof(ZDOMan))]
    internal static class DeadZdoEvictPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> RetainSeconds;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(DeadZdoEvictPatch),
                ValConfig.SectionServerMemory,
                "Evict Dead ZDO Records",
                false,
                "Drops the server's record of a destroyed object once it is a few minutes old. The " +
                "server keeps the id of every object destroyed since the world loaded, so a stale " +
                "copy a player sends back cannot bring it back, and never clears that list while the " +
                "world is loaded; every arrow, dropped item, felled tree and killed creature adds to " +
                "it for the life of the process. The stale copies it guards against arrive within " +
                "seconds of the destroy. Off by default.");

            RetainSeconds = ValConfig.BindServerConfig(
                ValConfig.SectionServerMemory,
                "Dead ZDO Retain Seconds",
                600f,
                "How long the record of a destroyed object is kept. Well above the seconds a stale " +
                "copy can be in flight; raise it rather than lower it if players ever report a " +
                "destroyed object coming back.",
                advanced: true,
                valMin: 60f,
                valMax: 3600f);
        }

        private struct Record {
            public ZDOID Uid;
            public float DiedAt;
            public long Stamp;
        }

        // Destroys are recorded in time order, so the oldest record is always at the head.
        private static readonly Queue<Record> Records = new Queue<Record>();
        private static long _evicted;

        // Bounded so a mass destroy expiring all at once cannot stall a frame.
        private const int EvictionsPerFrame = 1024;

        /// <summary>Records removed so far this session, for the stats line.</summary>
        internal static long Evicted => _evicted;

        /// <summary>Records waiting to expire.</summary>
        internal static int Pending => Records.Count;

        [HarmonyPostfix]
        [HarmonyPatch(nameof(ZDOMan.HandleDestroyedZDO))]
        private static void HandleDestroyedZdoPostfix(ZDOMan __instance, ZDOID uid) {
            if (Enabled == null || !Enabled.Value) { return; }
            if (!RunMode.IsServer) { return; }

            // Vanilla records only when the ZDO existed; read back what it wrote rather than
            // guess. The stamp lets the eviction tell this record from a later re-destroy.
            if (!__instance.m_deadZDOs.TryGetValue(uid, out long stamp)) { return; }

            Records.Enqueue(new Record { Uid = uid, DiedAt = Time.realtimeSinceStartup, Stamp = stamp });
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(ZDOMan.Update))]
        private static void UpdatePostfix(ZDOMan __instance) {
            if (Records.Count == 0) { return; }

            if (Enabled == null || !Enabled.Value) {
                Records.Clear();
                return;
            }

            float retain = RetainSeconds != null ? RetainSeconds.Value : 600f;
            float cutoff = Time.realtimeSinceStartup - retain;
            Dictionary<ZDOID, long> dead = __instance.m_deadZDOs;

            for (int i = 0; i < EvictionsPerFrame && Records.Count > 0; i++) {
                Record record = Records.Peek();
                if (record.DiedAt > cutoff) { break; }

                Records.Dequeue();

                if (dead.TryGetValue(record.Uid, out long stamp) && stamp == record.Stamp) {
                    dead.Remove(record.Uid);
                    _evicted++;
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(ZDOMan.ShutDown))]
        private static void ShutDownPostfix() {
            Records.Clear();
            Records.TrimExcess();
            _evicted = 0;
        }
    }
}
