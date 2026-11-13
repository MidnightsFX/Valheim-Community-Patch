using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Correctness {
    // Fix Armor Stand Cape Crash: stops the game crashing when an armor stand shows a cape.
    //
    // Since the Deep North update a cape is MagicaCloth cloth, and VisEquipment's m_clothColliders, the body shapes the
    // cape rests against, holds that library's colliders. The player prefab was re-authored for it, but both armor stand
    // prefabs still list their five Unity CapsuleColliders from the old cloth system. Unity loads them into the list
    // anyway, MagicaCloth reads each as one of its own colliders when the cape is built, and the game crashes outright
    // with MagicaCloth2.ColliderManager.AddColliderInternal on top of the stack. Hanging a cape on a stand does it, and so
    // does coming near a stand that wore one in a world from before the update.
    //
    // A postfix on VisEquipment.Awake replaces each CapsuleCollider in the list with a MagicaCloth capsule of the same
    // axis, center and size on the same object, so the cape still rests on the stand's body, and drops any other entry
    // that is not a MagicaCloth collider. It tests the entries rather than naming the prefabs, so a mod's copy of a stand
    // is covered too; the player's list is already right and is left alone.
    //
    // Both: every game that loads a stand builds its cape, and a dedicated server loads the objects around its own
    // position.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(VisEquipment))]
    internal static class ArmorStandCapeCrashPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ArmorStandCapeCrashPatch),
                ValConfig.SectionCorrectness,
                "Fix Armor Stand Cape Crash",
                true,
                "Stops the game crashing when an armor stand shows a cape, whether hung on it now or kept from a " +
                "world made before the Deep North update. The stands still point their capes at body shapes from " +
                "the old cloth system; matching ones for the new system take their place.");
        }

        // The cloth library's types are read off the field and its assembly, so VCP needs no reference to the library.
        // If an update renames any of them the conversion stands down; without the list type the whole fix does.
        private static readonly FieldInfo ClothCollidersField = AccessTools.Field(typeof(VisEquipment), "m_clothColliders");
        private static readonly Type ColliderType = ListElementType(ClothCollidersField?.FieldType);
        private static readonly Type CapsuleType = ColliderType?.Assembly.GetType("MagicaCloth2.MagicaCapsuleCollider");
        private static readonly FieldInfo CenterField = CapsuleType == null ? null : AccessTools.Field(CapsuleType, "center");
        private static readonly FieldInfo DirectionField = CapsuleType == null ? null : AccessTools.Field(CapsuleType, "direction");
        private static readonly MethodInfo SetSizeMethod = CapsuleType == null
            ? null
            : AccessTools.Method(CapsuleType, "SetSize", new[] { typeof(float), typeof(float), typeof(float) });
        private static readonly bool CanConvert = CenterField != null && DirectionField != null && SetSizeMethod != null;

        private static Type ListElementType(Type listType) {
            if (listType == null || !listType.IsGenericType || !typeof(IList).IsAssignableFrom(listType)) { return null; }
            return listType.GetGenericArguments()[0];
        }

        [HarmonyPostfix]
        [HarmonyPatch("Awake")]
        private static void AwakePostfix(VisEquipment __instance) {
            if (Enabled == null || !Enabled.Value || ColliderType == null) { return; }
            if (!(ClothCollidersField.GetValue(__instance) is IList colliders)) { return; }

            int replaced = 0;
            int dropped = 0;
            for (int i = colliders.Count - 1; i >= 0; i--) {
                object entry = colliders[i];
                if (entry == null || ColliderType.IsInstanceOfType(entry)) { continue; }

                Component capsule = entry is CapsuleCollider unity ? ToClothCapsule(unity) : null;
                if (capsule != null) {
                    colliders[i] = capsule;
                    replaced++;
                } else {
                    colliders.RemoveAt(i);
                    dropped++;
                }
            }

            if ((replaced > 0 || dropped > 0) && Logger.DebugEnabled) {
                Logger.LogDebug(
                    $"Cape colliders on '{__instance.transform.root.name}': replaced {replaced} Unity capsule(s), " +
                    $"dropped {dropped} other entr{(dropped == 1 ? "y" : "ies")}.");
            }
        }

        // MagicaCloth sizes a capsule as (start radius, end radius, length), and like Unity's height the length runs
        // end to end, caps included.
        private static Component ToClothCapsule(CapsuleCollider unity) {
            if (!CanConvert) { return null; }

            Component capsule = unity.gameObject.AddComponent(CapsuleType);
            if (capsule == null) { return null; }

            DirectionField.SetValue(capsule, Enum.ToObject(DirectionField.FieldType, unity.direction));
            CenterField.SetValue(capsule, unity.center);
            SetSizeMethod.Invoke(capsule, new object[] { unity.radius, unity.radius, unity.height });
            return capsule;
        }
    }
}
