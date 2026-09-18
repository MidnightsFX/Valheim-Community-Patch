using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

namespace ValheimCommunityPatch.Patches.Performance {
    // ZDO memory stats: the 'vcp_zdomem' server console command and the 'Log ZDO Memory Stats'
    // periodic line, reporting the sizes the server memory fixes act on.
    //
    // One line: connected peers, the sum and maximum of the per-peer sent-object tables, the
    // dead-object record count, live ZDO and ZDO pool counts, the per-type field table counts and
    // field table pool depths, what each memory fix has evicted or dropped so far, and the managed
    // heap. Every number is a Count or a cached counter, so it costs nothing to leave on.
    //
    // Server: the command is server-only and an admin can run it remotely; the periodic line only
    // writes on the host. Diagnostic; nothing here changes behaviour.
    [PatchSide(Side.Server)]
    internal static class ZdoMemoryStats {
        internal static ConfigEntry<bool> LogStats;
        internal static ConfigEntry<float> LogInterval;

        internal const string Command = "vcp_zdomem";

        internal static void BindConfig() {
            LogStats = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Log ZDO Memory Stats",
                false,
                "Diagnostic. Writes the line the '" + Command + "' console command prints to the log " +
                "every 'ZDO Memory Stats Interval' seconds on the server, so the effect of the " +
                "server memory fixes can be followed over an uptime.",
                advanced: true);

            LogInterval = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "ZDO Memory Stats Interval",
                300f,
                "Seconds between ZDO memory stats lines.",
                advanced: true,
                valMin: 10f,
                valMax: 3600f);
        }

        [HarmonyPatch(typeof(Terminal))]
        internal static class TerminalHook {
            [HarmonyPostfix]
            [HarmonyPatch("InitTerminal")]
            private static void InitTerminalPostfix() {
                new Terminal.ConsoleCommand(
                    Command,
                    "Prints the sizes of the server's ZDO bookkeeping (Valheim Community Patch).",
                    (Terminal.ConsoleEvent)(args => {
                        string line = Build();
                        args.Context?.AddString(line);
                        Logger.LogInfo(line);
                    }),
                    onlyServer: true,
                    remoteCommand: true);
            }
        }

        [HarmonyPatch(typeof(ZDOMan))]
        internal static class UpdateHook {
            private static float _next;

            [HarmonyPostfix]
            [HarmonyPatch(nameof(ZDOMan.Update))]
            private static void UpdatePostfix() {
                if (LogStats == null || !LogStats.Value) {
                    _next = 0f;
                    return;
                }

                if (!RunMode.IsServer) { return; }

                float now = Time.realtimeSinceStartup;
                if (now < _next) { return; }

                _next = now + (LogInterval != null ? LogInterval.Value : 300f);
                Logger.LogInfo(Build());
            }
        }

        // The approximate cost of one per-peer table entry, confirmed against a server core dump:
        // the Dictionary<ZDOID, PeerZDOInfo> Entry element is 28 bytes - hash and next (8), the
        // Pack=2 ZDOID key (6), and the PeerZDOInfo value (12) stored inline, since it is a struct -
        // plus a 4-byte bucket slot. Both arrays are over-allocated to the next prime capacity, so
        // the real figure runs above this.
        private const int BytesPerPeerEntry = 32;

        internal static string Build() {
            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null) { return "ZDO memory stats: no world loaded."; }

            List<ZDOMan.ZDOPeer> peers = zdoMan.m_peers;
            long tableSum = 0;
            int tableMax = 0;
            int forceSend = 0;
            int invalid = 0;
            for (int i = 0; i < peers.Count; i++) {
                int count = peers[i].m_zdos.Count;
                tableSum += count;
                if (count > tableMax) { tableMax = count; }
                forceSend += peers[i].m_forceSend.Count;
                invalid += peers[i].m_invalidSector.Count;
            }

            StringBuilder sb = new StringBuilder(512);
            sb.Append("ZDO memory stats: peers ").Append(peers.Count)
              .Append(" | peer tables: sum ").Append(tableSum.ToString("N0"))
              .Append(", max ").Append(tableMax.ToString("N0"))
              .Append(", ~").Append((tableSum * BytesPerPeerEntry / 1048576L).ToString("N0")).Append(" MB")
              .Append(", force-send ").Append(forceSend)
              .Append(", invalidated ").Append(invalid)
              .Append(" | dead records ").Append(zdoMan.m_deadZDOs.Count.ToString("N0"))
              .Append(" | zdos ").Append(zdoMan.m_objectsByID.Count.ToString("N0"))
              .Append(", zdo pool free ").Append(ZDOPool.GetPoolSize().ToString("N0"))
              .Append(" | field tables f/v/q/i/l/s/b ")
              .Append(ZDOExtraData.s_floats.Count.ToString("N0")).Append('/')
              .Append(ZDOExtraData.s_vec3.Count.ToString("N0")).Append('/')
              .Append(ZDOExtraData.s_quats.Count.ToString("N0")).Append('/')
              .Append(ZDOExtraData.s_ints.Count.ToString("N0")).Append('/')
              .Append(ZDOExtraData.s_longs.Count.ToString("N0")).Append('/')
              .Append(ZDOExtraData.s_strings.Count.ToString("N0")).Append('/')
              .Append(ZDOExtraData.s_byteArrays.Count.ToString("N0"))
              .Append(" | field table pools ")
              .Append(ZdoDataPoolTrimPatch.PoolDepth<float>()).Append('/')
              .Append(ZdoDataPoolTrimPatch.PoolDepth<Vector3>()).Append('/')
              .Append(ZdoDataPoolTrimPatch.PoolDepth<Quaternion>()).Append('/')
              .Append(ZdoDataPoolTrimPatch.PoolDepth<int>()).Append('/')
              .Append(ZdoDataPoolTrimPatch.PoolDepth<long>()).Append('/')
              .Append(ZdoDataPoolTrimPatch.PoolDepth<string>()).Append('/')
              .Append(ZdoDataPoolTrimPatch.PoolDepth<byte[]>())
              .Append(" | fixes: evicted ").Append(DeadZdoEvictPatch.Evicted.ToString("N0"))
              .Append(" (").Append(DeadZdoEvictPatch.Pending.ToString("N0")).Append(" pending)")
              .Append(", dropped tables ").Append(ZdoDataPoolTrimPatch.Dropped.ToString("N0"))
              .Append(" | managed heap ").Append((GC.GetTotalMemory(false) / 1048576L).ToString("N0")).Append(" MB");

            // Unity's view of the Mono heap; both report 0 where the player build does not expose them.
            long monoUsed = Profiler.GetMonoUsedSizeLong();
            long monoHeap = Profiler.GetMonoHeapSizeLong();
            if (monoHeap > 0) {
                sb.Append(", mono ").Append((monoUsed / 1048576L).ToString("N0"))
                  .Append(" of ").Append((monoHeap / 1048576L).ToString("N0")).Append(" MB");
            }

            return sb.ToString();
        }
    }
}
