using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect, two of them, in HeightmapBuilder.BuildThread (HeightmapBuilder.cs:66-94):
    //
    //  - Thread.Sleep(10) runs after *every* loop iteration, including one that just finished a
    //    build with more work queued. That hard-caps terrain generation at under a hundred maps a
    //    second and, in practice, far fewer - and every sleep with work queued directly extends
    //    ZoneSystem.SpawnZone's IsTerrainReady wait (a zone spawn retries on a 100 ms tick) and
    //    RequestTerrainSync's main-thread busy spin (HeightmapBuilder.cs:181-195, a do-while with
    //    no yield).
    //
    //  - The ready queue is capped at 16 entries with silent oldest-first eviction. TerrainLod alone
    //    keeps 9 distant-LOD results in flight, and a moving player's ghost-zone ring enqueues more
    //    via IsTerrainReady's add-if-absent side effect - so finished results get evicted before
    //    their heightmap consumes them and are rebuilt from scratch, wasting the thread the sleeps
    //    already starve.
    //
    // Fix: the same loop, with the sleep only when the queue was empty this iteration, and the
    // ready cap raised and configurable (a result is ~100 KB, so the default 32 holds ~3.5 MB).
    // Mutex discipline is copied exactly - same lock/release pairing, no new lock ordering. The
    // toggle and cap are re-read every iteration, so a server-synced change applies live.
    //
    // Constraint worth knowing: the builder thread enters BuildThread once, when the lazily-created
    // singleton starts it. A Harmony detour applied after that never takes effect for the running
    // thread. BepInEx applies this patch in plugin Awake, long before anything touches
    // HeightmapBuilder.instance, so in practice the patched body is what the thread runs; the
    // Prepare check below logs if that assumption is ever violated rather than failing silently.
    //
    // Both: servers generate terrain for every ghost zone around every peer; the throughput matters
    // most there.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(HeightmapBuilder))]
    internal static class HeightmapBuilderThroughputPatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> ReadyCap;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(HeightmapBuilderThroughputPatch),
                ValConfig.SectionPerformance,
                "Fix Terrain Builder Throughput",
                true,
                "Lets the terrain build thread work continuously while builds are queued instead of " +
                "sleeping 10ms after every single build, and keeps more finished results before old " +
                "ones are thrown away and rebuilt. Vanilla's pacing makes zones near a moving player " +
                "wait several extra ticks for their terrain.");

            ReadyCap = ValConfig.BindServerConfig(
                ValConfig.SectionPerformance,
                "Terrain Builder Ready Cap",
                32,
                "How many finished terrain results the build thread may hold before discarding the " +
                "oldest. Each is roughly 100 KB. Vanilla holds 16, which the distant-terrain ring " +
                "alone nearly fills.",
                advanced: true,
                valMin: 16,
                valMax: 128);
        }

        // The thread that would run the unpatched body starts the moment anything touches
        // HeightmapBuilder.instance. Patching happens in plugin Awake, before any scene code runs,
        // so the singleton cannot exist yet - but if some other mod's preloader created it, this
        // fix is silently inert for the session, and that deserves a log line.
        [HarmonyPrepare]
        private static bool Prepare() {
            if (HeightmapBuilder.m_instance != null) {
                Logger.LogWarning(
                    "The terrain build thread was already running before this patch applied, so " +
                    "'Fix Terrain Builder Throughput' is inert this session.");
            }

            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch("BuildThread")]
        private static bool BuildThreadPrefix(HeightmapBuilder __instance) {
            // Read once to decide which body this thread runs for its lifetime; the loop below
            // re-reads it so the fix can also stand down live if the toggle is turned off.
            if (Enabled == null || !Enabled.Value) { return true; }

            ZLog.Log((object)"Builder started");
            bool stop = false;
            while (!stop) {
                __instance.m_lock.WaitOne();
                bool haveWork = __instance.m_toBuild.Count > 0;
                __instance.m_lock.ReleaseMutex();

                if (haveWork) {
                    __instance.m_lock.WaitOne();
                    HeightmapBuilder.HMBuildData data = __instance.m_toBuild[0];
                    __instance.m_lock.ReleaseMutex();

                    __instance.Build(data);

                    __instance.m_lock.WaitOne();
                    __instance.m_toBuild.Remove(data);
                    __instance.m_ready.Add(data);
                    int cap = Enabled.Value && ReadyCap != null ? ReadyCap.Value : 16;
                    while (__instance.m_ready.Count > cap) { __instance.m_ready.RemoveAt(0); }
                    __instance.m_lock.ReleaseMutex();
                }

                // The whole fix: idle pacing only when idle (or when the toggle was switched off
                // mid-session, which restores vanilla's unconditional sleep).
                if (!haveWork || !Enabled.Value) { Thread.Sleep(10); }

                __instance.m_lock.WaitOne();
                stop = __instance.m_stop;
                __instance.m_lock.ReleaseMutex();
            }

            return false;
        }
    }
}
