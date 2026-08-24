using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: ZPackage.Write(ZPackage) copies the entire nested package onto the heap before
    // writing a single byte of it:
    //
    //   public void Write(ZPackage pkg) {
    //     byte[] array = pkg.GetArray();       // GetArray() is m_stream.ToArray() - a full copy
    //     this.m_writer.Write(array.Length);
    //     this.m_writer.Write(array);
    //   }
    //
    // That is the innermost step of ZDO synchronisation. ZDOMan.SendZDOs builds a package and hands it
    // to ZRpc.Invoke("ZDOData", pkg), which reaches ZRpc.Serialize -> m_pkg.Write(pkg) - so the whole
    // ZDO payload is duplicated on the heap once per peer, per send tick, on top of the copy
    // ZSteamSocket.Send already makes. Every routed RPC pays it too, and the receive path pays it
    // again. On a populated server that is sustained GC pressure buying nothing.
    //
    // Fix: write straight out of the source stream's backing buffer. The result is byte-identical -
    // BinaryWriter.Write(byte[]) emits raw bytes with no length prefix of its own, so writing the
    // length followed by buffer[0..length] produces exactly the same stream, and ToArray() already
    // ignored Position and returned the whole 0..Length range.
    //
    // On GetBuffer(), which is the one thing that could make this unsound: MemoryStream.GetBuffer()
    // throws UnauthorizedAccessException on a stream constructed over a caller-supplied array. It
    // cannot happen here. ZPackage.m_stream is a field initialiser (ZPackage.cs:15) and every
    // constructor - including ZPackage(byte[]) and ZPackage(string) - writes *into* that expandable
    // stream rather than wrapping the array, as does Load(). No path hands a ZPackage a
    // non-exposable buffer.
    //
    // Replacing the method outright rather than editing its IL: at three lines there is nothing left
    // to anchor a transpiler on, and every one of those lines changes. Same call as
    // PaintMaskStridePatch makes for the same reason.
    //
    // Provenance: same technique as the ZPackage.Write prefix in ComfyMods/Compress (GPL-3.0,
    // redseiko), taken on its own - that mod's opt-in GZip compression of ZDO data is a protocol
    // change needing both sides, and is deliberately not included.
    //
    // Both, and hottest on the server: this is pure serialisation with no GameObject involved, and
    // it sits under every ZDO data send and every routed RPC - once per peer per send tick.
    [PatchSide(Side.Both)]
    [HarmonyPatch(typeof(ZPackage))]
    internal static class ZPackageWriteAllocPatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ZPackageWriteAllocPatch),
                ValConfig.SectionPerformance,
                "Fix ZDO Packet Allocation",
                true,
                "Stops every nested network package being copied onto the heap before it is written. " +
                "Vanilla does this once per peer per send tick on the ZDO sync path, so on a busy " +
                "server it is constant garbage for no benefit. The bytes sent are unchanged.");
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ZPackage.Write), new[] { typeof(ZPackage) })]
        private static bool WritePrefix(ZPackage __instance, ZPackage pkg) {
            if (Enabled == null || !Enabled.Value) { return true; }

            // GetArray() flushed both of these before taking its copy; the length has to be read
            // after the flush or a buffered tail would be dropped.
            pkg.m_writer.Flush();
            pkg.m_stream.Flush();

            int length = (int)pkg.m_stream.Length;
            __instance.m_writer.Write(length);
            __instance.m_writer.Write(pkg.m_stream.GetBuffer(), 0, length);

            return false;
        }
    }
}
