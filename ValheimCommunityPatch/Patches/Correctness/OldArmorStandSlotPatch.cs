using System;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Correctness {
    // The Deep North upgrade converts only the first six armor stand slots, so shield, cape and utility read as empty.
    // Postfixes ArmorStand.Awake to convert each slot still holding a prefab name into the hash the stand now reads.
    // Old armor stands give their items back, also in worlds upgraded before this fix.
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
                "update, which the world upgrade left unreadable.");
        }

        [HarmonyPostfix]
        [HarmonyPatch("Awake")]
        private static void AwakePostfix(ArmorStand __instance) {
            if (Enabled == null || !Enabled.Value) { return; }
            ZNetView nview = __instance.m_nview;
            int[] keys = ArmorStand.s_itemKeyHashes;
            // The owner writes it: the host in a game it hosts, usually a nearby player on a dedicated server.
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

                // Clears only this machine's copy, since a received object keeps fields it is not sent;
                // harmless, as the stand reads only the hash.
                zdo.RemoveString(keys[i]);
            }

            if (converted > 0 && Logger.DebugEnabled) {
                Logger.LogDebug($"Armor stand at {__instance.transform.position}: converted {converted} old slot item(s).");
            }
        }
    }
}
