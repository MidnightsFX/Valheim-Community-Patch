using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // ZDOMan.m_deadZDOs keeps every destroyed object's id until the world reloads, so it grows for the server's life.
    // Postfixes ZDOMan.HandleDestroyedZDO to queue each id and ZDOMan.Update to evict those older than the retain time.
    // Server memory stops growing with every arrow, dropped item, felled tree and killed creature.
    [PatchSide(Side.Server)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZDOMan))]
    internal static class DeadZdoEvictPatch {
        internal static FixToggle Enabled;
        internal static ConfigEntry<float> RetainSeconds;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(DeadZdoEvictPatch),
                ValConfig.SectionServerMemory,
                "Evict Dead ZDO Records",
                false,
                "Lets the server forget a destroyed object after a few minutes instead of keeping every one until the world reloads.");

            RetainSeconds = ValConfig.BindServerConfig(
                ValConfig.SectionServerMemory,
                "Dead ZDO Retain Seconds",
                600f,
                "Seconds the server remembers a destroyed object, so a stale copy a player sends back cannot " +
                "restore it. Raise it rather than lower it if a destroyed object ever comes back.",
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
            // Vanilla records dead ids only on the host.
            if (!RunMode.IsServer) { return; }

            // Vanilla records only when the ZDO existed; read back what it wrote rather than
            // guess. The stamp lets the eviction tell this record from a later re-destroy.
            if (!__instance.m_deadZDOs.TryGetValue(uid, out long stamp)) { return; }

            // Real time, not the game-time stamp, which jumps forward when players sleep.
            Records.Enqueue(new Record { Uid = uid, DiedAt = Time.realtimeSinceStartup, Stamp = stamp });
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(ZDOMan.Update))]
        private static void UpdatePostfix(ZDOMan __instance) {
            if (Records.Count == 0) { return; }

            // Turned off, the pending records are forgotten and their entries stay, as vanilla leaves them.
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

                // The entry only stops a client's in-flight update resurrecting the object, and a client
                // drops its copy, and sends nothing more for it, once the routed destroy reaches it.
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
