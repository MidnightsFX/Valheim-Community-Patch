using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Defeat keys go to the client owning the dying boss, not to who fought it, so group members miss Hildir's quest keys.
    // Prefixes and postfixes Character.OnDeath to broadcast the key by routed RPC, checked against the hit record.
    // Every nearby player who fought the boss gets its defeat key, whichever client owned it.
    // Provenance: Zen.ModLib (catalogue), reimplemented with one globally registered RPC.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class BossKeySharePatch {
        internal static FixToggle Enabled;
        internal static ConfigEntry<float> MaxDistance;
        internal static ConfigEntry<bool> RequireHit;

        private const string RpcName = "VCP_BossKillCredit";

        // Hildir's quest bosses are not flagged as bosses; their keys are the ones this exists for.
        private const string HildirBossKeyPrefix = "BossHildir";

        // Held only for the length of a boss's OnDeath with nobody credited, so it cannot set its world key.
        private static string _withheldWorldKey;

        private sealed class Kill {
            public string Key;
            public Vector3 Position;
            public bool IsOwner;
            public bool RequireHit;
            public int TakeBackFrom = -1;
            public List<string> Hitters;
        }

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(BossKeySharePatch),
                ValConfig.SectionCorrectness,
                "Share Boss Defeat Keys",
                true,
                "Gives every nearby player credit for a boss kill, not only the player whose game owned the boss.");

            MaxDistance = ValConfig.BindServerConfig(
                ValConfig.SectionCorrectness,
                "Boss Defeat Key Range",
                300f,
                "How far from the boss a player can be when it dies and still be credited with the defeat key, in meters.",
                false, 0f, 2000f);

            RequireHit = ValConfig.BindServerConfig(
                ValConfig.SectionCorrectness,
                "Boss Defeat Key Requires Hit",
                true,
                "Credits a boss kill, Hildir's quest bosses included, only to players in range who hit it, and sets the " +
                "world key only when one of them is there. Off, everyone in range is credited and the world key is " +
                "always set; needs Share Boss Defeat Keys on.");
        }

        // Game.Start is where vanilla registers its own routed RPCs. Guarded because
        // ZRoutedRpc.Register throws on a second registration.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Game), nameof(Game.Start))]
        private static void GameStartPostfix() {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null) { return; }
            if (rpc.m_functions.ContainsKey(RpcName.GetStableHashCode())) { return; }

            rpc.Register<ZPackage>(RpcName, RPC_BossKillCredit);
        }

        private static void RPC_BossKillCredit(long sender, ZPackage pkg) {
            if (Enabled == null || !Enabled.Value) { return; }

            string key = pkg.ReadString();
            Vector3 position = pkg.ReadVector3();
            bool requireHit = pkg.ReadBool();
            if (string.IsNullOrEmpty(key)) { return; }

            Player localPlayer = Player.m_localPlayer;
            if (localPlayer == null) { return; }
            if (Utils.DistanceXZ(localPlayer.transform.position, position) > MaxDistance.Value) { return; }

            if (requireHit) {
                string name = localPlayer.GetPlayerName();
                bool hit = false;
                for (int i = pkg.ReadInt(); i > 0 && !hit; i--) { hit = pkg.ReadString() == name; }
                if (!hit) { return; }

                // The owner holds the world key back when it sees no credited player, which misses a
                // credited player beyond the area it has loaded.
                ZoneSystem zones = ZoneSystem.instance;
                if (zones != null && !zones.GetGlobalKey(key)) { zones.SetGlobalKey(key); }
            }

            localPlayer.AddUniqueKey(key);
        }

        // Read before OnDeath runs: it destroys the boss's ZDO, and the hit record and ownership with it.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
        private static void OnDeathPrefix(Character __instance, out Kill __state) {
            __state = null;
            if (Enabled == null || !Enabled.Value) { return; }

            string key = __instance.m_defeatSetGlobalKey;
            if (string.IsNullOrEmpty(key)) { return; }

            ZNetView nview = __instance.m_nview;
            if (nview == null || !nview.IsValid()) { return; }

            Kill kill = new Kill {
                Key = key,
                Position = __instance.transform.position,
                IsOwner = nview.IsOwner(),
                RequireHit = RequireHit.Value && (__instance.IsBoss() || key.StartsWith(HildirBossKeyPrefix, StringComparison.Ordinal)),
            };

            if (kill.RequireHit) {
                ZDO zdo = nview.GetZDO();
                Player localPlayer = Player.m_localPlayer;

                // Vanilla queues the key on every client that runs OnDeath: the owner, and for a boss with a
                // death animation every client that plays it.
                if (localPlayer == null || !IsCredited(zdo, localPlayer, kill.Position)) {
                    kill.TakeBackFrom = Player.m_addUniqueKeyQueue.Count;
                }

                if (kill.IsOwner) {
                    kill.Hitters = Hitters(zdo);
                    if (!AnyCreditedPlayer(zdo, kill.Position)) { _withheldWorldKey = key; }
                    Logger.LogDebug($"Boss kill {key}: {kill.Hitters.Count} player(s) hit it, world key {(_withheldWorldKey == null ? "set" : "withheld")}.");
                }
            }

            __state = kill;
        }

        // Priority.First: the world key is released before any other mod's postfix runs.
        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
        private static void OnDeathPostfix(Kill __state, bool __runOriginal) {
            _withheldWorldKey = null;
            if (__state == null || !__runOriginal) { return; }

            // The queue is only applied at the player's next spawn, so it can still hold this key from
            // an earlier kill the player was credited for; take back only the copy OnDeath just added.
            if (__state.TakeBackFrom >= 0) {
                List<string> queue = Player.m_addUniqueKeyQueue;
                int added = queue.LastIndexOf(__state.Key);
                if (added >= __state.TakeBackFrom) { queue.RemoveAt(added); }
            }
            if (__state.IsOwner) { Broadcast(__state); }
        }

        // OnDeath can throw part way; a key left withheld would swallow the next legitimate kill's.
        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
        private static void OnDeathFinalizer() {
            _withheldWorldKey = null;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.SetGlobalKey), typeof(string))]
        private static bool SetGlobalKeyPrefix(string name) {
            return _withheldWorldKey == null || name != _withheldWorldKey;
        }

        // A vanilla server still relays this: HandleRoutedRPC ignores an unknown hash and forwards the message as normal.
        private static void Broadcast(Kill kill) {
            if (ZRoutedRpc.instance == null) { return; }

            ZPackage pkg = new ZPackage();
            pkg.Write(kill.Key);
            pkg.Write(kill.Position);
            pkg.Write(kill.RequireHit);
            if (kill.RequireHit) {
                pkg.Write(kill.Hitters.Count);
                foreach (string name in kill.Hitters) { pkg.Write(name); }
            }

            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcName, pkg);
        }

        // The hit record: RPC_Damage marks every player who lands a hit on the boss, by name, and
        // OnDeath credits each of them with the kill in their stats. The marking hit counts even when
        // its fire or poison does the damage later, through a status effect that carries no attacker.
        private static bool HasHit(ZDO zdo, string playerName) {
            return zdo.GetBool(ZDOVars.s_attackers + playerName);
        }

        private static bool IsCredited(ZDO zdo, Player player, Vector3 bossPosition) {
            return Utils.DistanceXZ(player.transform.position, bossPosition) <= MaxDistance.Value
                && HasHit(zdo, player.GetPlayerName());
        }

        // Every player whose character this client has loaded, which takes in those near the boss.
        private static bool AnyCreditedPlayer(ZDO zdo, Vector3 bossPosition) {
            foreach (Player player in Player.GetAllPlayers()) {
                if (IsCredited(zdo, player, bossPosition)) { return true; }
            }
            return false;
        }

        private static List<string> Hitters(ZDO zdo) {
            List<string> names = new List<string>();
            if (ZNet.instance == null) { return names; }

            foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList()) {
                if (HasHit(zdo, info.m_name)) { names.Add(info.m_name); }
            }
            return names;
        }
    }
}
