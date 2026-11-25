using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // A station feed RPC that arrives after an owner change or once full is dropped, destroying the item already taken.
    // Prefixes on six smelter, fireplace, cooking station and fermenter handlers drop the item back and skip vanilla.
    // Players get a rejected item back at their feet, or beside the station, instead of losing it.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch]
    internal static class StationRefundPatch {
        internal static FixToggle Enabled;

        private const string FixName = "Refund Rejected Station Items";

        // Also named by the plugin's soft dependencies, which load these first so Prepare can see them.
        internal const string EternalFireGuid = "digitalroot.mods.eternalfire";
        internal const string AutomaticFuelGuid = "TastyChickenLegs.AutomaticFuel";

        private static readonly string[] UnpaidFeederGuids = { EternalFireGuid, AutomaticFuelGuid };

        // The first Eternal Fire release that no longer sends a feed message of its own.
        // System.Version: the game declares a global Version class of its own.
        private static readonly System.Version EternalFirePaidVersion = new System.Version(1, 1, 6);

        private static bool _loggedStandDown;

        private const string NotOwner = "this peer no longer owns it";
        private const string Full = "it is full";
        private const string NotAccepted = "it does not accept that item";

        private static readonly MethodInfo HandleRoutedRpcMethod = AccessTools.Method(
            typeof(ZNetView), nameof(ZNetView.HandleRoutedRPC), new[] { typeof(ZRoutedRpc.RoutedRPCData) });

        // Without the dispatch hook no message ever looks addressed here and nothing is refunded,
        // which is the safe failure; the check exists so that failure is logged rather than silent.
        private static readonly HookHealth Health = new HookHealth(
            typeof(StationRefundPatch),
            FixName, () => PatchHelper.HasHook(HandleRoutedRpcMethod, typeof(StationRefundPatch)));

        // The peer the message being dispatched was addressed to. Zero between dispatches, which is
        // also the value of a broadcast, and neither is ever refunded: a message to an unowned
        // station reaches every peer, and one refund each would duplicate the item.
        private static long _target;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(StationRefundPatch),
                ValConfig.SectionCorrectness,
                FixName,
                true,
                "Drops an item back at the player when a smelter, kiln, fireplace, cooking station or " +
                "fermenter rejects it on arrival, instead of destroying it; inactive with AutomaticFuel or " +
                "Eternal Fire older than 1.1.6.");
        }

        // AutomaticFuel and Eternal Fire before 1.1.6 send these messages without taking an item, or
        // send more than the station has room for, so a refund would be an item made from nothing.
        // Eternal Fire 1.1.6 sends none: an eternal station reports full fuel instead.
        [HarmonyPrepare]
        private static bool Prepare() {
            List<string> found = new List<string>();
            bool oldEternalFire = false;
            foreach (string guid in UnpaidFeederGuids) {
                if (!Chainloader.PluginInfos.TryGetValue(guid, out PluginInfo info)) { continue; }

                string name = info?.Metadata?.Name ?? guid;
                if (guid == EternalFireGuid) {
                    // An unreadable version is treated as an old one, which is the safe failure.
                    System.Version version = info?.Metadata?.Version;
                    if (version != null && version >= EternalFirePaidVersion) { continue; }

                    oldEternalFire = true;
                    if (version != null) { name += $" {version}"; }
                }

                found.Add(name);
            }

            if (found.Count == 0) { return true; }

            if (!_loggedStandDown) {
                _loggedStandDown = true;
                bool one = found.Count == 1;
                Logger.LogWarning(
                    $"'{FixName}' is disabled because {string.Join(" and ", found)} {(one ? "is" : "are")} " +
                    $"loaded. {(one ? "It adds" : "They add")} fuel to stations without taking an item, so " +
                    "refunding would create items from nothing. Item loss when using fermenters, ovens and " +
                    "cooking stations is likely in multiplayer." +
                    (oldEternalFire ? $" Eternal Fire {EternalFirePaidVersion} and later no longer does this." : ""));
            }

            return false;
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
