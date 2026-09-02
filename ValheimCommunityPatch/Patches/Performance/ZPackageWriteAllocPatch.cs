using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix ZDO Packet Allocation: writing one ZPackage into another no longer copies the whole
    // payload onto the heap first.
    //
    // ZPackage.Write(ZPackage) calls pkg.GetArray(), which is MemoryStream.ToArray(): a full copy
    // of the nested package. That is the innermost step of ZDO synchronisation (ZDOMan.SendZDOs ->
    // ZRpc.Invoke -> ZRpc.Serialize), so the whole ZDO payload is duplicated once per peer per send
    // tick, and again for every routed RPC and every receive.
    //
    // A prefix writes the length and then the bytes straight out of the source stream's backing
    // buffer. The output is byte-identical. GetBuffer() cannot throw here: every ZPackage
    // constructor writes into its own expandable stream rather than wrapping a caller's array.
    //
    // Both, hottest on the server. Provenance: ComfyMods/Compress (GPL-3.0, redseiko), taken
    // without that mod's GZip protocol change.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ZPackage))]
    internal static class ZPackageWriteAllocPatch {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ZPackage.Write), new[] { typeof(ZPackage) })]
        private static bool WritePrefix(ZPackage __instance, ZPackage pkg) {
            // GetArray() flushed both before copying; the length must be read after the flush.
            pkg.m_writer.Flush();
            pkg.m_stream.Flush();

            int length = (int)pkg.m_stream.Length;
            __instance.m_writer.Write(length);
            __instance.m_writer.Write(pkg.m_stream.GetBuffer(), 0, length);

            return false;
        }
    }
}
