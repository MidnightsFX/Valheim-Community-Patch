using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix ZDO Packet Allocation: ZPackage.Write(ZPackage) copies the nested payload to a new array every write.
    // A prefix writes the length and bytes straight from the source stream's buffer, byte-identical to vanilla.
    // ZDO sync, routed RPCs and receives stop duplicating each payload on the heap, the server most of all.
    // Provenance: ComfyMods/Compress (GPL-3.0, redseiko), without that mod's GZip protocol change.
    [PatchSide(Side.Both)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZPackage))]
    internal static class ZPackageWriteAllocPatch {
        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(ZPackageWriteAllocPatch));

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ZPackage.Write), new[] { typeof(ZPackage) })]
        private static bool WritePrefix(ZPackage __instance, ZPackage pkg) {
            if (ApiSwitch.Off) { return true; }

            // GetArray() flushed both before copying; the length must be read after the flush.
            pkg.m_writer.Flush();
            pkg.m_stream.Flush();

            // GetBuffer() cannot throw: every ZPackage constructor writes into its own expandable
            // stream rather than wrapping a caller's array.
            int length = (int)pkg.m_stream.Length;
            __instance.m_writer.Write(length);
            __instance.m_writer.Write(pkg.m_stream.GetBuffer(), 0, length);

            return false;
        }
    }
}
