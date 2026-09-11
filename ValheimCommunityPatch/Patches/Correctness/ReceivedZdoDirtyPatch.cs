using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Unsaved Client Changes: an object a connected player placed or changed is written to
    // disk by the next world save, instead of only once something else in its chunk changes.
    //
    // Since the chunked save format, a world save rewrites only the chunks in ZDOMan's dirty set;
    // a chunk that is not dirty keeps its old file. That set is fed by ZDO.IncreaseDataRevision,
    // ZDO.IncreaseOwnerRevision and the sector add or remove of a persistent ZDO, all of which are
    // local writes. ZDOMan.RPC_ZDOData does none of them for a ZDO received from a peer: a new one
    // is filed in its sector while Persistent is still false, Deserialize sets the flag through
    // the property, and the revisions are assigned directly. So a piece a remote client built, or
    // the contents a client put in a chest, reach disk only if something else in the same 512 m
    // chunk dirties it before the last save, and are gone after a server restart otherwise.
    //
    // A postfix on ZDO.Deserialize, whose only caller is RPC_ZDOData, marks the ZDO's chunk dirty
    // through SetDirtySector, vanilla's own entry point and the one IncreaseDataRevision uses, so
    // the save sees exactly the event a local write would have raised. A portal marks the portal
    // store dirty instead, since portals are saved from their own store rather than from a chunk.
    //
    // Server: only the host saves the world, gated at runtime because a client process can start
    // hosting later.
    [PatchSide(Side.Server)]
    [HarmonyPatch(typeof(ZDO))]
    internal static class ReceivedZdoDirtyPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ReceivedZdoDirtyPatch),
                ValConfig.SectionCorrectness,
                "Fix Unsaved Client Changes",
                true,
                "Marks an object a connected player placed or changed for the next world save. The chunked " +
                "save format only rewrites chunks the game has marked as changed, and it never marks one " +
                "for a change that arrives from another player, so anything a client built or stored can " +
                "be missing after a server restart.");
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(ZDO.Deserialize))]
        private static void DeserializePostfix(ZDO __instance) {
            if (Enabled == null || !Enabled.Value) { return; }
            if (!RunMode.IsServer) { return; }
            if (!__instance.Persistent) { return; }

            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null) { return; }

            // Portals are saved from their own store, so a portal's chunk is never rewritten for it.
            Game game = Game.instance;
            if (!ReferenceEquals(game, null) && game.PortalPrefabHash.Contains(__instance.GetPrefab())) {
                zdoMan.SetDirtyPortals();
                return;
            }

            // The position was stored before Deserialize ran, so this is the sector it was filed in.
            zdoMan.SetDirtySector(__instance);
        }
    }
}
