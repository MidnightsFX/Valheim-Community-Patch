using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Collision Contact Allocation: with reused callbacks, Collision.contacts allocates a new array per callback.
    // Transpiles Character and FloatingTerrain.OnCollisionStay and ImpactEffect.OnCollisionEnter to use reused buffers.
    // Physics callbacks that fire every fixed step stop feeding the garbage collector.
    [PatchSide(Side.Both)]
    [HarmonyPatch]
    internal static class CollisionContactsAllocPatch {
        private const int MaxCachedContacts = 64;

        private static readonly ContactPoint[][] Buffers = new ContactPoint[MaxCachedContacts + 1][];
        private static readonly ContactPoint[] Empty = new ContactPoint[0];

        private static readonly MethodInfo ContactsGetter =
            AccessTools.PropertyGetter(typeof(Collision), nameof(Collision.contacts));
        private static readonly MethodInfo ReusedContactsMethod =
            AccessTools.Method(typeof(CollisionContactsAllocPatch), nameof(ReusedContacts));

        // Same values, same order, same Length as Collision.contacts; only the lifetime differs. Unity drops the
        // cached array between reused callbacks. One buffer per exact count keeps the compiled foreach and
        // ImpactEffect's .Length unchanged; no call site holds it across anything that asks for contacts again.
        private static ContactPoint[] ReusedContacts(Collision collision) {
            if (collision == null) { return Empty; }

            int count = collision.contactCount;
            if (count <= 0) { return Empty; }
            if (count > MaxCachedContacts) { return collision.contacts; }

            ContactPoint[] buffer = Buffers[count];
            if (buffer == null) {
                buffer = new ContactPoint[count];
                Buffers[count] = buffer;
            }

            collision.GetContacts(buffer);
            return buffer;
        }

        // Priority.Last on all three: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(Character), "OnCollisionStay")]
        private static IEnumerable<CodeInstruction> CharacterOnCollisionStayTranspiler(IEnumerable<CodeInstruction> instructions) =>
            PatchHelper.ReplaceCalls(instructions, ContactsGetter, ReusedContactsMethod, "Character.OnCollisionStay", expected: 1);

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(ImpactEffect), nameof(ImpactEffect.OnCollisionEnter))]
        private static IEnumerable<CodeInstruction> ImpactEffectOnCollisionEnterTranspiler(IEnumerable<CodeInstruction> instructions) =>
            PatchHelper.ReplaceCalls(instructions, ContactsGetter, ReusedContactsMethod, "ImpactEffect.OnCollisionEnter", expected: 2);

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(FloatingTerrain), "OnCollisionStay")]
        private static IEnumerable<CodeInstruction> FloatingTerrainOnCollisionStayTranspiler(IEnumerable<CodeInstruction> instructions) =>
            PatchHelper.ReplaceCalls(instructions, ContactsGetter, ReusedContactsMethod, "FloatingTerrain.OnCollisionStay", expected: 1);
    }
}
