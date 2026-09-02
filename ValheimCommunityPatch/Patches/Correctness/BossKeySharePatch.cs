using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Share Boss Defeat Keys: every nearby player gets credit for a boss kill, not only the one
    // whose client owned the boss.
    //
    // Character.OnDeath queues the per-player defeat key into Player.m_addUniqueKeyQueue, but
    // OnDeath only runs on the client that owns the dying character. The world-wide global key
    // replicates; the per-player one does not, so in a group only one player records it. Most
    // visible with Hildir's quest bosses, which track completion by the per-player key.
    //
    // The owner broadcasts the key through a routed RPC and every client that was within range
    // applies it locally. AddUniqueKey is a HashSet add, so the owner re-applying its own broadcast
    // is harmless.
    //
    // Client on both ends. A vanilla server still relays the RPC: an unknown hash makes
    // ZRoutedRpc.HandleRoutedRPC return quietly and the message is forwarded as normal.
    // Provenance: Zen.ModLib (catalogue), reimplemented with one globally registered RPC.
    [PatchSide(Side.Client)]
    [HarmonyPatch]
    internal static class BossKeySharePatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> MaxDistance;

        private const string RpcName = "VCP_ShareDefeatKey";

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(BossKeySharePatch),
                ValConfig.SectionCorrectness,
                "Share Boss Defeat Keys",
                true,
                "Gives every nearby player credit for a boss kill. Vanilla records the per-player defeat " +
                "key only on whichever client happened to own the boss, so in a group everyone else is " +
                "left without it - most noticeably for Hildir's quest bosses.");

            MaxDistance = ValConfig.BindServerConfig(
                ValConfig.SectionCorrectness,
                "Boss Defeat Key Range",
                300f,
                "How far from the boss a player can be and still be credited with the defeat key, in metres.",
                false, 0f, 2000f);
        }

        // Game.Start is where vanilla registers its own routed RPCs. Guarded because
        // ZRoutedRpc.Register throws on a second registration.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Game), nameof(Game.Start))]
        private static void GameStartPostfix() {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null) { return; }
            if (rpc.m_functions.ContainsKey(RpcName.GetStableHashCode())) { return; }

            rpc.Register<string, Vector3>(RpcName, RPC_ShareDefeatKey);
        }

        private static void RPC_ShareDefeatKey(long sender, string key, Vector3 position) {
            if (Enabled == null || !Enabled.Value) { return; }
            if (string.IsNullOrEmpty(key)) { return; }

            Player localPlayer = Player.m_localPlayer;
            if (localPlayer == null) { return; }

            if (Utils.DistanceXZ(localPlayer.transform.position, position) > MaxDistance.Value) { return; }

            localPlayer.AddUniqueKey(key);
        }

        // Players have no m_defeatSetGlobalKey, so this is inert for player deaths.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Character), "OnDeath")]
        private static void OnDeathPostfix(Character __instance) {
            if (Enabled == null || !Enabled.Value) { return; }
            if (string.IsNullOrEmpty(__instance.m_defeatSetGlobalKey)) { return; }

            ZNetView nview = __instance.m_nview;
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) { return; }
            if (ZRoutedRpc.instance == null) { return; }

            ZRoutedRpc.instance.InvokeRoutedRPC(
                ZRoutedRpc.Everybody, RpcName, __instance.m_defeatSetGlobalKey, __instance.transform.position);
        }
    }
}
