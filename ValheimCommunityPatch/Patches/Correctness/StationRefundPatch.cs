using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Refund Rejected Station Items: an item that a station's network handler would silently discard
    // is dropped back at the player who sent it.
    //
    // Feeding a smelter, kiln, fireplace, cooking station or fermenter removes the item from the
    // inventory first and then sends an RPC to whichever peer owned the station when the message
    // was sent. The handler credits the station only if it still owns it and, for a fireplace,
    // cooking station or fermenter, only if there is still room; otherwise it returns and the item
    // is gone. Ownership moves whenever a player claims the station or walks away, and two players
    // feeding the last slot at once both pass their local checks, so either condition can be false
    // by the time the message arrives.
    //
    // Prefixes on the six handlers repeat the guards vanilla applies and, where vanilla would discard
    // the item, spawn it back and skip the handler: at the sender's feet, inside auto-pickup range,
    // when their character is loaded on this machine, otherwise beside the station. Only a message
    // addressed to this peer is refunded. A message to an unowned station is broadcast to every peer,
    // and one refund per peer would duplicate the item, so a hook on ZNetView.HandleRoutedRPC records
    // the target of the message being dispatched and a broadcast is left to vanilla. Fix Fuel And Ore
    // Loss keeps the sender's own smelter and fireplace messages local; this covers the sender running
    // without it, the fermenter and cooking station, and the full-on-arrival race.
    //
    // Both: the handler runs on whichever peer the message names, and a listen host or a dedicated
    // server can own a station.
    [PatchSide(Side.Both)]
    [HarmonyPatch]
    internal static class StationRefundPatch {
        internal static ConfigEntry<bool> Enabled;

        private const string FixName = "Refund Rejected Station Items";

        private const string NotOwner = "this peer no longer owns it";
        private const string Full = "it is full";
        private const string NotAccepted = "it does not accept that item";

        private static readonly MethodInfo HandleRoutedRpcMethod = AccessTools.Method(
            typeof(ZNetView), nameof(ZNetView.HandleRoutedRPC), new[] { typeof(ZRoutedRpc.RoutedRPCData) });

        // Without the dispatch hook no message ever looks addressed here and nothing is refunded,
        // which is the safe failure; the check exists so that failure is logged rather than silent.
        private static readonly HookHealth Health = new HookHealth(
            FixName, () => PatchHelper.HasHook(HandleRoutedRpcMethod, typeof(StationRefundPatch)));

        // The peer the message being dispatched was addressed to. Zero between dispatches, which is
        // also the value of a broadcast, and neither is ever refunded.
        private static long _target;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(StationRefundPatch),
                ValConfig.SectionCorrectness,
                FixName,
                true,
                "Drops an item back at the player when a smelter, kiln, fireplace, cooking station or " +
                "fermenter rejects it on arrival. Vanilla removes the item from your inventory and then " +
                "sends a network message that is silently discarded if the station changed owner, or " +
                "filled up, in the meantime, destroying the item.");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.HandleRoutedRPC), typeof(ZRoutedRpc.RoutedRPCData))]
        private static void HandleRoutedRpcPrefix(ZRoutedRpc.RoutedRPCData rpcData, out long __state) {
            // Saved and restored because a handler can dispatch a further RPC locally.
            __state = _target;
            _target = rpcData == null ? 0L : rpcData.m_targetPeerID;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.HandleRoutedRPC), typeof(ZRoutedRpc.RoutedRPCData))]
        private static void HandleRoutedRpcPostfix(long __state) => _target = __state;

        /// <summary>True when the fix is on and the message being dispatched named this peer alone.</summary>
        private static bool Armed(ZNetView nview) {
            if (Enabled == null || !Enabled.Value) { return false; }
            if (nview == null || !nview.IsValid()) { return false; }
            if (!Health.Healthy) { return false; }

            return _target != 0L && _target == ZDOMan.GetSessionID();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Smelter), "RPC_AddOre")]
        private static bool SmelterAddOrePrefix(Smelter __instance, long sender, string name, bool cheated) {
            ZNetView nview = __instance.m_nview;
            if (!Armed(nview)) { return true; }

            string why = !nview.IsOwner() ? NotOwner : !__instance.IsItemAllowed(name) ? NotAccepted : null;
            if (why == null) { return true; }

            Refund(__instance, Prefab(name), cheated, sender, why);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Smelter), "RPC_AddFuel")]
        private static bool SmelterAddFuelPrefix(Smelter __instance, long sender) {
            ZNetView nview = __instance.m_nview;
            if (!Armed(nview) || nview.IsOwner()) { return true; }

            Refund(__instance, Prefab(__instance.m_fuelItem), false, sender, NotOwner);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fireplace), "RPC_AddFuel")]
        private static bool FireplaceAddFuelPrefix(Fireplace __instance, long sender) {
            ZNetView nview = __instance.m_nview;
            if (!Armed(nview)) { return true; }

            string why = !nview.IsOwner() ? NotOwner
                : Mathf.CeilToInt(nview.GetZDO().GetFloat(ZDOVars.s_fuel)) >= __instance.m_maxFuel ? Full
                : null;
            if (why == null) { return true; }

            Refund(__instance, Prefab(__instance.m_fuelItem), false, sender, why);
            return false;
        }

        // The item handler has no owner check in vanilla: a peer that is not the owner writes the slot
        // into its own copy of the ZDO. Refunding there too could duplicate, so only the two vanilla
        // guards are mirrored.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CookingStation), "RPC_AddItem")]
        private static bool CookingStationAddItemPrefix(CookingStation __instance, long sender, string itemName, bool cheated) {
            ZNetView nview = __instance.m_nview;
            if (!Armed(nview)) { return true; }

            string why = !__instance.IsItemAllowed(itemName) ? NotAccepted : __instance.GetFreeSlot() == -1 ? Full : null;
            if (why == null) { return true; }

            Refund(__instance, Prefab(itemName), cheated, sender, why);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CookingStation), "RPC_AddFuel")]
        private static bool CookingStationAddFuelPrefix(CookingStation __instance, long sender) {
            ZNetView nview = __instance.m_nview;
            if (!Armed(nview) || nview.IsOwner()) { return true; }

            Refund(__instance, Prefab(__instance.m_fuelItem), false, sender, NotOwner);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fermenter), "RPC_AddItem")]
        private static bool FermenterAddItemPrefix(Fermenter __instance, long sender, int nameHash, bool cheated) {
            ZNetView nview = __instance.m_nview;
            if (!Armed(nview)) { return true; }

            string why = !nview.IsOwner() ? NotOwner
                : __instance.GetStatus() != Fermenter.Status.Empty ? Full
                : !__instance.IsItemAllowed(nameHash) ? NotAccepted
                : null;
            if (why == null) { return true; }

            Refund(__instance, Prefab(nameHash), cheated, sender, why);
            return false;
        }

        private static GameObject Prefab(string name) => ZNetScene.instance == null ? null : ZNetScene.instance.GetPrefab(name);

        private static GameObject Prefab(int hash) => ZNetScene.instance == null ? null : ZNetScene.instance.GetPrefab(hash);

        private static GameObject Prefab(ItemDrop item) => item == null ? null : item.gameObject;

        /// <summary>
        /// Spawns one <paramref name="prefab"/> for the sender, the way a destroyed station drops its
        /// contents: at the sender's feet when their character is loaded here, else beside the station.
        /// </summary>
        private static void Refund(MonoBehaviour station, GameObject prefab, bool cheated, long sender, string why) {
            if (prefab == null || prefab.GetComponent<ItemDrop>() == null) {
                Logger.LogDebug($"{FixName}: {station.name} rejected an item because {why}, but no item prefab matches it, so nothing was refunded.");
                return;
            }

            Player player = FindPlayer(sender);
            Vector3 origin = player != null ? player.transform.position : station.transform.position;
            Vector3 position = origin + Vector3.up + Random.insideUnitSphere * 0.3f;
            Quaternion rotation = Quaternion.Euler(0f, Random.Range(0, 360), 0f);

            ItemDrop drop = Object.Instantiate(prefab, position, rotation).GetComponent<ItemDrop>();
            drop.m_itemData.m_stack = 1;
            ItemDrop.OnCreateNew(drop, cheated && !PlayerProfile.s_bypassCheatChecks);

            Logger.LogDebug(
                $"{FixName}: {station.name} rejected {prefab.name} from peer {sender} because {why}; " +
                (player != null ? "dropped it at their feet." : "dropped it beside the station."));
        }

        // The routed RPC sender id is the sender's session id, which is also the owner of its player ZDO.
        private static Player FindPlayer(long peer) {
            foreach (Player player in Player.GetAllPlayers()) {
                ZNetView nview = player.m_nview;
                if (nview != null && nview.IsValid() && nview.GetZDO().GetOwner() == peer) { return player; }
            }

            return null;
        }
    }
}
