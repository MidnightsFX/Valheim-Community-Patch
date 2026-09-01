using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: Collision.contacts is a property that builds and returns a brand new
    // ContactPoint[] on every read, and Valheim reads it from inside physics callbacks that fire
    // every fixed step:
    //
    //   Character.OnCollisionStay (Character.cs:1678)  foreach (ContactPoint c in collision.contacts)
    //   ImpactEffect.OnCollisionEnter (ImpactEffect.cs:41, :47)  info.contacts.Length, info.contacts[0]
    //   FloatingTerrain.OnCollisionStay (FloatingTerrain.cs:100)  collision.contacts[0].normal
    //
    // Character.OnCollisionStay is the one that matters: every character the local machine owns
    // gets one callback per contacting collider per fixed step, so at 50 Hz a populated area is
    // thousands of throwaway arrays a second, each carrying several ContactPoint structs. The
    // array is read once and dropped. ImpactEffect reads the property twice for one collision,
    // allocating two arrays where it uses one.
    //
    // Fix: swap the property read for an exactly sized buffer, reused per contact count and
    // filled through Collision.GetContacts - which is the same data the property copies out,
    // just written into an array we already own. Sizing the buffer to contactCount rather than
    // handing back one oversized scratch array is what keeps this a single edit: the compiled
    // foreach in Character.OnCollisionStay bounds itself with Ldlen, and .Length is read directly
    // in ImpactEffect, so a buffer whose Length is the real count needs no second rewrite and
    // stays correct for any caller a future game version adds.
    //
    // Reuse is safe here because these three call sites never hold the array across anything that
    // could ask for contacts again. Character's foreach body only reads structs out of it;
    // ImpactEffect copies contacts[0] into a local before it does any work; FloatingTerrain reads
    // one normal. Unity dispatches collision callbacks from the physics step rather than
    // re-entrantly from user code, and the one nested case in the game -
    // FloatingTerrainDummy.OnCollisionStay forwarding to FloatingTerrain.OnDummyCollision
    // (FloatingTerrainDummy.cs:14-19) - passes the Collision along without reading contacts
    // itself. Main thread only, for the same reason.
    //
    // Above the cache ceiling the property is used unchanged, so an unusually complex contact
    // manifold costs exactly what it costs in vanilla rather than growing a buffer nothing else
    // will ever reuse.
    //
    // Both: a dedicated server instantiates only what falls inside its own active area at world
    // origin, so it has few characters - but the ones it has run the identical callback, and the
    // cost of patching a method a server barely reaches is one unused trampoline.
    [PatchSide(Side.Both)]
    [HarmonyPatch]
    internal static class CollisionContactsAllocPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(CollisionContactsAllocPatch),
                ValConfig.SectionPerformance,
                "Fix Collision Contact Allocation",
                true,
                "Stops the physics collision callbacks allocating a fresh contact-point array on " +
                "every read. Every character the machine owns does this on every physics step, so " +
                "in a busy area it is thousands of throwaway arrays a second. The contact data " +
                "handed to the game is identical. Changing this requires a game restart.");
        }

        // Contact manifolds are small; this covers every count these three call sites realistically
        // see, and the arrays are created only for the counts actually observed.
        private const int MaxCachedContacts = 64;

        private static readonly ContactPoint[][] Buffers = new ContactPoint[MaxCachedContacts + 1][];
        private static readonly ContactPoint[] Empty = new ContactPoint[0];

        private static readonly MethodInfo ContactsGetter =
            AccessTools.PropertyGetter(typeof(Collision), nameof(Collision.contacts));
        private static readonly MethodInfo ReusedContactsMethod =
            AccessTools.Method(typeof(CollisionContactsAllocPatch), nameof(ReusedContacts));

        /// <summary>
        /// <see cref="Collision.contacts"/> without the per-read array. Same values, same order,
        /// same Length - only the array's lifetime differs.
        /// </summary>
        private static ContactPoint[] ReusedContacts(Collision collision) {
            // Collision is a plain managed class, not a UnityEngine.Object, so this is a reference
            // check and not a native alive-check.
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

        // Priority.Last, for the reason in ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(Character), "OnCollisionStay")]
        private static IEnumerable<CodeInstruction> CharacterOnCollisionStayTranspiler(
            IEnumerable<CodeInstruction> instructions) =>
            Rewrite(instructions, "Character.OnCollisionStay", 1);

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(ImpactEffect), nameof(ImpactEffect.OnCollisionEnter))]
        private static IEnumerable<CodeInstruction> ImpactEffectOnCollisionEnterTranspiler(
            IEnumerable<CodeInstruction> instructions) =>
            Rewrite(instructions, "ImpactEffect.OnCollisionEnter", 2);

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(FloatingTerrain), "OnCollisionStay")]
        private static IEnumerable<CodeInstruction> FloatingTerrainOnCollisionStayTranspiler(
            IEnumerable<CodeInstruction> instructions) =>
            Rewrite(instructions, "FloatingTerrain.OnCollisionStay", 1);

        private static IEnumerable<CodeInstruction> Rewrite(
            IEnumerable<CodeInstruction> instructions, string method, int expected) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);
            if (Enabled == null || !Enabled.Value) { return codes; }

            if (ContactsGetter == null || ReusedContactsMethod == null) { return instructions; }

            int replaced = 0;

            for (int i = 0; i < codes.Count; i++) {
                if (!codes[i].Calls(ContactsGetter)) { continue; }

                // callvirt on a sealed managed class becomes a static call taking the instance;
                // the stack shape is unchanged either way.
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = ReusedContactsMethod;
                replaced++;
            }

            if (replaced != expected) {
                Logger.LogWarning(
                    $"{method}: expected {expected} Collision.contacts read(s), found {replaced}, so " +
                    "that callback keeps vanilla's per-read allocation. Another mod has most likely " +
                    "already rewritten the method - if so, nothing is wrong.");
                return instructions;
            }

            Logger.LogDebug($"{method}: {replaced} Collision.contacts read(s) routed through a reused buffer.");
            return codes;
        }
    }
}
