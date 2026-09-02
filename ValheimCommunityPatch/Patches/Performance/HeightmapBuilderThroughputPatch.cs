using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Terrain Builder Throughput: the terrain build thread sleeps only when idle and holds
    // more finished results.
    //
    // HeightmapBuilder.BuildThread sleeps 10 ms after every iteration, including one that just
    // finished a build with more work queued, which caps terrain generation far below what the
    // thread could do and extends every zone spawn's wait for terrain. Its ready queue is capped
    // at 16 with silent oldest-first eviction, and the distant-terrain ring alone keeps 9 in
    // flight, so finished results are evicted before they are consumed and rebuilt from scratch.
    //
    // A prefix replaces the loop with the same code and the same mutex discipline, sleeping only
    // when the queue was empty, and with a configurable ready cap (default 32). The thread enters
    // BuildThread once, when the singleton is created, so the patch has to be in place before
    // that; Prepare logs if it was not.
    //
    // Both: servers generate terrain for every ghost zone around every peer.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(HeightmapBuilder))]
    internal static class HeightmapBuilderThroughputPatch {
        internal static ConfigEntry<int> ReadyCap;

        internal static void BindConfig() {
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
                    int cap = ReadyCap != null ? ReadyCap.Value : 16;
                    while (__instance.m_ready.Count > cap) { __instance.m_ready.RemoveAt(0); }
                    __instance.m_lock.ReleaseMutex();
                }

                if (!haveWork) { Thread.Sleep(10); }

                __instance.m_lock.WaitOne();
                stop = __instance.m_stop;
                __instance.m_lock.ReleaseMutex();
            }

            return false;
        }
    }
}
