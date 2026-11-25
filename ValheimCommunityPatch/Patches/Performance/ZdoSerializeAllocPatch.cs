using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCommunityPatch.Patches.Performance {
    // ZDO.Serialize copies all seven field tables into new lists, with seven delegates, per object per peer per send.
    // Prefixes ZDO.Serialize with the same writes, read straight from each table's key and value arrays.
    // A busy server makes far less garbage on its send path, and the bytes sent are identical.
    [PatchSide(Side.Server)]
    [ModDisableable]
    [HarmonyPatch(typeof(ZDO))]
    internal static class ZdoSerializeAllocPatch {
        internal static FixToggle Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(ZdoSerializeAllocPatch),
                ValConfig.SectionServerMemory,
                "Fix ZDO Serialize Allocation",
                false,
                "Writes an object's fields to the network straight from storage instead of copying them " +
                "into new lists for every player on every send, so a full server makes less garbage.");
        }

        // Verified once: the tables' Keys and Values must be the backing arrays themselves, which a game
        // update could change; if not, vanilla runs for the session.
        private static readonly bool ArraysReachable = Probe();

        private static bool Probe() {
            try {
                BinarySearchDictionary<int, float> table = new BinarySearchDictionary<int, float>();
                table.Add(1, 1f);
                return table.Keys is int[] && table.Values is float[];
            } catch (Exception) {
                return false;
            }
        }

        // Priority.Last and __runOriginal, so a mod that cancels or steers Serialize keeps its say.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(nameof(ZDO.Serialize))]
        private static bool SerializePrefix(ZDO __instance, ZPackage pkg, bool __runOriginal) {
            if (!__runOriginal) { return false; }
            if (Enabled == null || !Enabled.Value) { return true; }
            // Only the host's send path, which runs for every peer, is hot enough to matter.
            if (!ArraysReachable || !RunMode.IsServer) { return true; }

            Write(__instance, pkg);
            return false;
        }

        // Vanilla's Serialize, step for step, over the tables instead of copies of them. Both read the
        // tables in their sorted order, so the bytes match, field order included.
        private static void Write(ZDO zdo, ZPackage pkg) {
            ZDOID uid = zdo.m_uid;

            BinarySearchDictionary<int, float> floats = Table(ZDOExtraData.s_floats, uid);
            BinarySearchDictionary<int, Vector3> vec3s = Table(ZDOExtraData.s_vec3, uid);
            BinarySearchDictionary<int, Quaternion> quats = Table(ZDOExtraData.s_quats, uid);
            BinarySearchDictionary<int, int> ints = Table(ZDOExtraData.s_ints, uid);
            BinarySearchDictionary<int, long> longs = Table(ZDOExtraData.s_longs, uid);
            BinarySearchDictionary<int, string> strings = Table(ZDOExtraData.s_strings, uid);
            BinarySearchDictionary<int, byte[]> byteArrays = Table(ZDOExtraData.s_byteArrays, uid);
            ZDOConnection connection = ZDOExtraData.GetConnection(uid);

            ZDO.ExtraDataFlags flags = ZDO.ExtraDataFlags.None;
            if (connection != null && connection.m_type != ZDOExtraData.ConnectionType.None) { flags |= ZDO.ExtraDataFlags.Connections; }
            if (Count(floats) > 0) { flags |= ZDO.ExtraDataFlags.Floats; }
            if (Count(vec3s) > 0) { flags |= ZDO.ExtraDataFlags.Vec3; }
            if (Count(quats) > 0) { flags |= ZDO.ExtraDataFlags.Quaternions; }
            if (Count(ints) > 0) { flags |= ZDO.ExtraDataFlags.Ints; }
            if (Count(longs) > 0) { flags |= ZDO.ExtraDataFlags.Longs; }
            if (Count(strings) > 0) { flags |= ZDO.ExtraDataFlags.Strings; }
            if (Count(byteArrays) > 0) { flags |= ZDO.ExtraDataFlags.ByteArrays; }

            bool rotated = zdo.m_rotation != Quaternion.identity.eulerAngles;
            if (zdo.Persistent) { flags |= ZDO.ExtraDataFlags.Persistent; }
            if (zdo.Distant) { flags |= ZDO.ExtraDataFlags.Distant; }
            flags |= (ZDO.ExtraDataFlags)((uint)zdo.Type << 10);
            if (rotated) { flags |= ZDO.ExtraDataFlags.Rotation; }

            pkg.Write((ushort)flags);
            pkg.Write(zdo.m_prefab);
            if (rotated) { pkg.Write(zdo.m_rotation); }

            if ((flags & ZDO.ExtraDataFlags.AnyLow8Bits) == 0) { return; }

            if ((flags & ZDO.ExtraDataFlags.Connections) != 0) {
                pkg.Write((byte)connection.m_type);
                pkg.Write(connection.m_target);
            }

            WriteTable(pkg, floats, WriteFloat);
            WriteTable(pkg, vec3s, WriteVec3);
            WriteTable(pkg, quats, WriteQuat);
            WriteTable(pkg, ints, WriteInt);
            WriteTable(pkg, longs, WriteLong);
            WriteTable(pkg, strings, WriteString);
            WriteTable(pkg, byteArrays, WriteBytes);
        }

        private static BinarySearchDictionary<int, T> Table<T>(Dictionary<ZDOID, BinarySearchDictionary<int, T>> container, ZDOID uid) {
            return container.TryGetValue(uid, out BinarySearchDictionary<int, T> table) ? table : null;
        }

        private static int Count<T>(BinarySearchDictionary<int, T> table) => table == null ? 0 : table.Count;

        // ZDODataHelper.WriteData, over the arrays. The delegates are allocated once below.
        private static void WriteTable<T>(ZPackage pkg, BinarySearchDictionary<int, T> table, Action<ZPackage, T> write) {
            int count = Count(table);
            if (count == 0) { return; }

            pkg.WriteNumItems(count);

            int[] keys = (int[])table.Keys;
            T[] values = (T[])table.Values;
            for (int i = 0; i < count; i++) {
                pkg.Write(keys[i]);
                write(pkg, values[i]);
            }
        }

        private static readonly Action<ZPackage, float> WriteFloat = (pkg, value) => pkg.Write(value);
        private static readonly Action<ZPackage, Vector3> WriteVec3 = (pkg, value) => pkg.Write(value);
        private static readonly Action<ZPackage, Quaternion> WriteQuat = (pkg, value) => pkg.Write(value);
        private static readonly Action<ZPackage, int> WriteInt = (pkg, value) => pkg.Write(value);
        private static readonly Action<ZPackage, long> WriteLong = (pkg, value) => pkg.Write(value);
        private static readonly Action<ZPackage, string> WriteString = (pkg, value) => pkg.Write(value);
        private static readonly Action<ZPackage, byte[]> WriteBytes = (pkg, value) => pkg.Write(value);
    }
}
