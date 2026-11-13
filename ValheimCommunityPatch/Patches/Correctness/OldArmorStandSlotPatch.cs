using System;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Old Armor Stand Slots: gives back the shield, cape and utility item of armor stands from worlds made before
    // the Deep North update.
    //
    // Older saves store each armor stand slot's item ("0_item" to "8_item") as its prefab name; the game now reads a
    // prefab hash from an int of the same key. Upgrading an old world converts only "0_item" to "5_item"
    // (ZDOMan.ConvertPrefabStrings), so the shield, cape and utility slots keep their names and the stand reads them as
    // empty. A cape still shows, from the stand's own equipment data, but the item cannot be taken back, and the three
    // slots drop nothing when the stand is destroyed.
    //
    // A postfix on ArmorStand.Awake converts every slot that still holds a name and no hash, the way the upgrade does
    // the first six, on the machine that owns the stand. Working per stand as it loads, rather than scanning the world
    // at load, also repairs worlds that were upgraded before the fix. Removing the name only clears this machine's copy:
    // a received object keeps fields it is not sent, which is harmless because the stand reads only the hash.
    //
    // Both: the stand's owner writes the conversion, the host in a game it hosts and usually a nearby player on a
    // dedicated server.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(ArmorStand))]
    internal static class OldArmorStandSlotPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(OldArmorStandSlotPatch),
                ValConfig.SectionCorrectness,
                "Fix Old Armor Stand Slots",
                true,
                "Gives back the shield, cape and utility item on armor stands from worlds made before the Deep North " +
                "update. Upgrading the world leaves those slots in the old format, so the stand treats them as empty: " +
                "a cape still shows but cannot be taken back, and the items are lost when the stand is destroyed.");
        }

        [HarmonyPostfix]
        [HarmonyPatch("Awake")]
        private static void AwakePostfix(ArmorStand __instance) {
            if (Enabled == null || !Enabled.Value) { return; }
            ZNetView nview = __instance.m_nview;
            int[] keys = ArmorStand.s_itemKeyHashes;
            if (nview == null || !nview.IsValid() || !nview.IsOwner() || keys == null) { return; }

            ZDO zdo = nview.GetZDO();
            int converted = 0;
            int slots = Math.Min(__instance.m_slots.Count, keys.Length);
            for (int i = 0; i < slots; i++) {
                string name = zdo.GetString(keys[i]);
                if (string.IsNullOrEmpty(name)) { continue; }

                if (zdo.GetInt(keys[i]) == 0) {
                    zdo.Set(keys[i], name.GetStableHashCode());
                    converted++;
                }
                zdo.RemoveString(keys[i]);
            }

            if (converted > 0 && Logger.DebugEnabled) {
                Logger.LogDebug($"Armor stand at {__instance.transform.position}: converted {converted} old slot item(s).");
            }
        }
    }
}
