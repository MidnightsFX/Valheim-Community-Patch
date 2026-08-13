using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Vanilla defect: a boss's defeat key is only recorded for one player in the group.
    //
    // Character.OnDeath queues the *per-player* unique key:
    //
    //   if (!string.IsNullOrEmpty(this.m_defeatSetGlobalKey))
    //     Player.m_addUniqueKeyQueue.Add(this.m_defeatSetGlobalKey);
    //
    // and later sets the world-wide key via ZoneSystem.SetGlobalKey, which does replicate. But OnDeath
    // is reached from CheckDeath, and Character.CustomUpdate only runs that whole block when the ZDO is
    // owned locally - so the unique key is added on exactly one client: whoever happened to own the
    // boss. Everyone else in the fight gets no credit.
    //
    // Player-visible symptom: kill a boss as a group and only one player has it recorded. Most visible
    // with Hildir's quest bosses, whose completion tracking is keyed off the per-player unique key
    // rather than the global one.
    //
    // Fix: the owner broadcasts the key to everyone, and each client applies it locally if it was near
    // enough to have taken part. AddUniqueKey is backed by a HashSet, so the owner re-applying its own
    // broadcast is harmless.
    //
    // Provenance: same defect and approach as Zen.ModLib's FixAddPlayerKeyOnBossDeath; reimplemented
    // here against a single globally-registered routed RPC rather than one registered per Character,
    // which avoids adding a handler to every creature ZNetView in the world.
    [HarmonyPatch]
    internal static class BossKeySharePatch {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> MaxDistance;

        private const string RpcName = "VCP_ShareDefeatKey";

        internal static void BindConfig() {
            Enabled = ValConfig.BindServerConfig(
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

        // Game.Start is where vanilla registers its own routed RPCs, so ZRoutedRpc exists by now and is
        // freshly built for this session. Registration is guarded because ZRoutedRpc.Register uses
        // Dictionary.Add and would throw on a second call - which would take Game.Start down with it.
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

        // Character.OnDeath already ran the owner-only local add by this point; this fans the same key
        // out to everyone else. Player overrides OnDeath, and players have no m_defeatSetGlobalKey, so
        // patching the base is both sufficient and inert for player deaths.
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
