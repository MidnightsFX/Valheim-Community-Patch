using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Server Map Rebuild: a client generates a server's world map again on every join, a multi-second freeze.
    // Prefixes GenerateWorldMap to load the map from a keyed cache file, and SaveMapTextureDataToDisk to write one.
    // Rejoining a server loads its world map from disk instead of generating it again.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Minimap))]
    internal static class ServerMapCachePatch {
        internal const string ExpandWorldSizeGuid = "expand_world_size";
        private const string ExpandWorldDataGuid = "expand_world_data";
        private const string ValheimAvxMinimapOwner = "com.kewb.valheimavx.minimap";
        private const string FixName = "Fix Server Map Rebuild";

        // Part of the key and the header: bump it when either changes, and old files stop matching.
        private const int FormatVersion = 3;
        private const int SampleGrid = 64;
        private const int KeepFiles = 10;
        private const string FolderName = "vcp_map_cache";
        private const string Extension = ".map";
        private const string TempExtension = ".map.tmp";
        private const uint Magic = 0x4D504356; // "VCPM"

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(ServerMapCachePatch));

        // Built at first use, once every mod has patched and Expand World Size's hooks are known.
        private static TakeoverCheck[] _takeovers;

        // One write at a time, on a pool thread; the main thread never waits for it.
        private static readonly object WriteGate = new object();

        private static int _warned;

        private static bool _minimapHeightResolved;
        private static Func<float, Heightmap.Biome, float> _minimapHeight;

        private sealed class Snapshot {
            internal string Path;
            internal byte[] Key;
            internal int[] Formats;
            internal int Size;
            internal byte[][] Data;
        }

        // Priority.Last and __runOriginal: a mod that generates the map its own way goes first. Other mods' postfixes
        // still run on a served map, as after a generated one.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Minimap.GenerateWorldMap))]
        [HarmonyPriority(Priority.Last)]
        private static bool GenerateWorldMapPrefix(Minimap __instance, bool __runOriginal) {
            if (!__runOriginal || ExpandWorldSize.Active || !Applies()) { return true; }

            return !TryLoad(__instance);
        }

        // The game calls this right after it filled the textures, before other mods' postfixes on
        // GenerateWorldMap can draw on them. Expand World Size calls it after those postfixes, so
        // its maps are saved at its upload instead.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Minimap.SaveMapTextureDataToDisk))]
        private static void SaveMapTextureDataToDiskPrefix(Minimap __instance) {
            if (ExpandWorldSize.Active || !Applies()) { return; }

            try {
                Texture2D[] textures = Textures(__instance);
                Save(textures, Key(__instance, __instance.m_textureSize, __instance.m_pixelSize, textures));
            } catch (Exception ex) {
                WarnOnce($"the map could not be saved for the next join. {ex.GetType().Name}: {ex.Message}");
            }
        }

        // A client of a remote server; a host has the game's own cache.
        private static bool Applies() {
            if (ApiSwitch.Off) { return false; }
            if (ZNet.instance == null || ZNet.instance.IsServer() || ZNet.World == null) { return false; }
            if (WorldGenerator.instance == null) { return false; }

            if (_takeovers == null) {
                // Any other mod with a prefix that can skip GenerateWorldMap or TryLoadMinimapTextureData makes this
                // fix stand down. Valheim AVX's prefixes pass through when an earlier prefix skipped the method, and
                // its cache load serves only a hosted world's own files, never a client's.
                string[] ignored = ExpandWorldSize.Active
                    ? new[] { ExpandWorldSizeGuid, ValheimAvxMinimapOwner }
                    : new[] { ValheimAvxMinimapOwner };
                _takeovers = new[] {
                    new TakeoverCheck(
                        typeof(ServerMapCachePatch),
                        AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.GenerateWorldMap)),
                        HookKinds.BoolPrefixes,
                        owners => $"Map generation is handled by {owners}, so '{FixName}' stands down.",
                        ignored),
                    new TakeoverCheck(
                        typeof(ServerMapCachePatch),
                        AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.TryLoadMinimapTextureData)),
                        HookKinds.BoolPrefixes,
                        owners => $"Map caching is handled by {owners}, so '{FixName}' stands down.",
                        ignored),
                };
            }

            // Each asked, so each logs its own stand-down.
            bool takenOver = false;
            foreach (TakeoverCheck check in _takeovers) { takenOver |= check.TakenOver; }
            return !takenOver;
        }

        private static Texture2D[] Textures(Minimap map) =>
            new[] { map.m_mapTexture, map.m_forestMaskTexture, map.m_heightTexture };

        private static int[] Formats(Texture2D[] textures) =>
            Array.ConvertAll(textures, texture => (int)texture.graphicsFormat);

        private static bool TryLoad(Minimap map) {
            Stopwatch watch = Stopwatch.StartNew();
            string path = null;
            try {
                Texture2D[] textures = Textures(map);
                int size = map.m_textureSize;
                byte[] key = Key(map, size, map.m_pixelSize, textures);
                path = PathFor(key);
                if (!File.Exists(path)) {
                    LogMiss();
                    return false;
                }

                byte[][] data;
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16)) {
                    int[] lengths = ReadHeader(file, key, Formats(textures), size);
                    for (int i = 0; lengths != null && i < textures.Length; i++) {
                        if (textures[i].width != size || textures[i].height != size
                            || lengths[i] != textures[i].GetRawTextureData<byte>().Length) {
                            lengths = null;
                        }
                    }

                    if (lengths == null) {
                        LogMismatch();
                        return false;
                    }

                    data = ReadPayload(file, lengths);
                }

                // A texture left half loaded is overwritten by the generation the caller falls back to.
                for (int i = 0; i < textures.Length; i++) {
                    textures[i].LoadRawTextureData(data[i]);
                    textures[i].Apply();
                }

                // Marks the file recently used, so trimming keeps it.
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            } catch (Exception ex) {
                WarnOnce($"the cached map could not be loaded, so it is generated again. {ex.GetType().Name}: {ex.Message}");
                if (path != null) { TryDelete(path); }
                return false;
            }

            LogHit(watch);
            return true;
        }

        private static void LogMiss() =>
            Logger.LogInfo($"{FixName}: this server's world map is not cached yet, so it is generated and saved for " +
                           "the next join.");

        private static void LogMismatch() =>
            Logger.LogInfo($"{FixName}: the cached map for this server's world does not fit this map, so it is " +
                           "generated again.");

        private static void LogHit(Stopwatch watch) =>
            Logger.LogInfo($"{FixName}: loaded this server's world map from the cache in {watch.ElapsedMilliseconds} ms.");

        // The world, game version, map size and formats, and the generator's output on a 64 x 64 grid, so a world
        // generation mod or setting that changes the map changes the key; one whose settings arrive after the first map
        // gets a file for each state.
        private static byte[] Key(Minimap map, int size, float pixelSize, Texture2D[] textures) {
            using (MemoryStream buffer = new MemoryStream(1 << 17))
            using (BinaryWriter writer = new BinaryWriter(buffer, Encoding.UTF8)) {
                World world = ZNet.World;
                writer.Write(FormatVersion);
                writer.Write(global::Version.CurrentVersion.ToString());
                writer.Write(world.m_name ?? "");
                writer.Write(world.m_seedName ?? "");
                writer.Write(world.m_seed);
                writer.Write(world.m_uid);
                writer.Write(world.m_worldGenVersion);
                writer.Write(size);
                writer.Write(pixelSize);
                foreach (Texture2D texture in textures) { writer.Write((int)texture.graphicsFormat); }

                foreach (float value in Samples(map, size, pixelSize)) { writer.Write(value); }

                writer.Flush();
                using (SHA256 sha = SHA256.Create()) {
                    return sha.ComputeHash(buffer.GetBuffer(), 0, (int)buffer.Length);
                }
            }
        }

        private const int SampleValues = 11;

        // The same inputs the generation reads at those pixels, through the same methods, so other
        // mods' patches on them count too.
        private static float[] Samples(Minimap map, int size, float pixelSize) {
            Func<float, Heightmap.Biome, float> minimapHeight = MinimapHeight();
            WorldGenerator generator = WorldGenerator.instance;
            int half = size / 2;
            float halfPixel = pixelSize / 2f;
            int step = Math.Max(1, size / SampleGrid);
            int perAxis = (size - step / 2 + step - 1) / step;
            float[] values = new float[perAxis * perAxis * SampleValues];

            for (int row = 0; row < perAxis; row++) {
                float wy = (row * step + step / 2 - half) * pixelSize + halfPixel;
                for (int column = 0; column < perAxis; column++) {
                    float wx = (column * step + step / 2 - half) * pixelSize + halfPixel;
                    Heightmap.Biome biome = generator.GetBiome(wx, wy);
                    float height = generator.GetBiomeHeight(biome, wx, wy, out _);
                    Color pixel = map.GetPixelColor(biome);
                    Color mask = map.GetMaskColor(wx, wy, height, biome);

                    int at = (row * perAxis + column) * SampleValues;
                    values[at] = (int)biome;
                    values[at + 1] = height;
                    values[at + 2] = pixel.r;
                    values[at + 3] = pixel.g;
                    values[at + 4] = pixel.b;
                    values[at + 5] = pixel.a;
                    values[at + 6] = mask.r;
                    values[at + 7] = mask.g;
                    values[at + 8] = mask.b;
                    values[at + 9] = mask.a;
                    values[at + 10] = minimapHeight != null ? minimapHeight(height, biome) : 0f;
                }
            }

            return values;
        }

        // Expand World Data shades the map per biome after the generator has answered, in the
        // generation's own code; its settings for that reach the key through its API.
        private static Func<float, Heightmap.Biome, float> MinimapHeight() {
            if (_minimapHeightResolved) { return _minimapHeight; }

            _minimapHeightResolved = true;
            if (Chainloader.PluginInfos.TryGetValue(ExpandWorldDataGuid, out PluginInfo info) && info.Instance != null) {
                MethodInfo method = info.Instance.GetType().Assembly.GetType("ExpandWorldData.Api")?.GetMethod(
                    "GetMinimapHeight", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(float), typeof(Heightmap.Biome) }, null);
                if (method != null && method.ReturnType == typeof(float)) {
                    _minimapHeight = (Func<float, Heightmap.Biome, float>)Delegate.CreateDelegate(
                        typeof(Func<float, Heightmap.Biome, float>), method);
                }
            }

            return _minimapHeight;
        }

        private static string Folder() =>
            Path.Combine(global::Utils.GetSaveDataPath(FileHelpers.FileSource.Local), FolderName);

        // The world's name keeps the folder readable; the key keeps the files apart.
        private static string PathFor(byte[] key) {
            StringBuilder name = new StringBuilder();
            foreach (char c in ZNet.World.m_name ?? "") {
                if (name.Length >= 32) { break; }
                name.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }

            name.Append('_');
            for (int i = 0; i < 8; i++) { name.Append(key[i].ToString("x2")); }

            return Path.Combine(Folder(), name + Extension);
        }

        // Each texture's byte length, or null when the file is for another map.
        private static int[] ReadHeader(Stream stream, byte[] key, int[] formats, int size) {
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true)) {
                if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion) { return null; }

                byte[] stored = reader.ReadBytes(key.Length);
                if (stored.Length != key.Length) { return null; }
                for (int i = 0; i < key.Length; i++) {
                    if (stored[i] != key[i]) { return null; }
                }

                if (reader.ReadInt32() != formats.Length) { return null; }

                long pixels = (long)size * size;
                int[] lengths = new int[formats.Length];
                for (int i = 0; i < formats.Length; i++) {
                    int format = reader.ReadInt32();
                    int width = reader.ReadInt32();
                    int height = reader.ReadInt32();
                    int length = reader.ReadInt32();
                    if (format != formats[i] || width != size || height != size || length <= 0 || length % pixels != 0) {
                        return null;
                    }

                    lengths[i] = length;
                }

                return lengths;
            }
        }

        private static byte[][] ReadPayload(Stream stream, int[] lengths) {
            byte[][] data = new byte[lengths.Length][];
            using (GZipStream gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true)) {
                for (int i = 0; i < lengths.Length; i++) {
                    data[i] = new byte[lengths[i]];
                    ReadExactly(gzip, data[i]);
                }
            }

            return data;
        }

        private static void ReadExactly(Stream stream, byte[] buffer) {
            int offset = 0;
            while (offset < buffer.Length) {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0) { throw new EndOfStreamException("the cache file is cut short"); }
                offset += read;
            }
        }

        // Main thread: copies the textures, then compresses and writes them on a pool thread.
        private static void Save(Texture2D[] textures, byte[] key) {
            Snapshot snapshot = new Snapshot {
                Path = PathFor(key),
                Key = key,
                Formats = Formats(textures),
                Size = textures[0].width,
                Data = Array.ConvertAll(textures, texture => texture.GetRawTextureData()),
            };

            ThreadPool.QueueUserWorkItem(_ => Write(snapshot));
        }

        private static void Write(Snapshot snapshot) {
            lock (WriteGate) {
                // Written aside and moved into place, so a quit mid-write never leaves a cut file
                // under a real name.
                string temp = snapshot.Path.Substring(0, snapshot.Path.Length - Extension.Length) + TempExtension;
                try {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(snapshot.Path));
                    using (FileStream file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)) {
                        using (BinaryWriter writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true)) {
                            writer.Write(Magic);
                            writer.Write(FormatVersion);
                            writer.Write(snapshot.Key);
                            writer.Write(snapshot.Data.Length);
                            for (int i = 0; i < snapshot.Data.Length; i++) {
                                writer.Write(snapshot.Formats[i]);
                                writer.Write(snapshot.Size);
                                writer.Write(snapshot.Size);
                                writer.Write(snapshot.Data[i].Length);
                            }
                        }

                        using (GZipStream gzip = new GZipStream(file, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) {
                            foreach (byte[] blob in snapshot.Data) { gzip.Write(blob, 0, blob.Length); }
                        }
                    }

                    if (File.Exists(snapshot.Path)) { File.Delete(snapshot.Path); }
                    File.Move(temp, snapshot.Path);
                    Logger.LogInfo($"{FixName}: saved this server's world map for the next join " +
                                   $"({new FileInfo(snapshot.Path).Length / 1024} KiB).");

                    Trim(System.IO.Path.GetDirectoryName(snapshot.Path));
                } catch (Exception ex) {
                    TryDelete(temp);
                    WarnOnce($"the map could not be saved for the next join. {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // Called under WriteGate, so every temp file left is from a write that never finished.
        private static void Trim(string folder) {
            FileInfo[] files = new DirectoryInfo(folder).GetFiles();
            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

            int kept = 0;
            foreach (FileInfo file in files) {
                if (file.Name.EndsWith(TempExtension, StringComparison.OrdinalIgnoreCase)) {
                    TryDelete(file.FullName);
                } else if (file.Name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) && ++kept > KeepFiles) {
                    TryDelete(file.FullName);
                }
            }
        }

        private static void TryDelete(string path) {
            try {
                File.Delete(path);
            } catch (Exception) {
                // Trimmed again after the next save.
            }
        }

        private static void WarnOnce(string message) {
            if (Interlocked.Exchange(ref _warned, 1) == 0) { Logger.LogWarning($"{FixName}: {message}"); }
            else { Logger.LogDebug($"{FixName}: {message}"); }
        }

        // Expand World Size generates the map on worker threads whatever other GenerateWorldMap prefixes return,
        // so its maps are served at its Generate step and saved at the GenerateWorldMap call it makes after upload.
        internal static class ExpandWorldSize {
            private static readonly Type[] GenerateParameters = {
                typeof(Minimap), typeof(int), typeof(float), typeof(Color32[]), typeof(Color32[]), typeof(Color[]),
                typeof(float[]), typeof(CancellationToken),
            };

            private static bool _resolved;
            private static bool? _active;
            internal static MethodInfo Generate;
            private static FieldInfo _fakeGenerate;

            // The miss whose upload is saved: main thread only, replaced by every Generate step.
            private static PendingSave _pending;

            private sealed class PendingSave {
                internal byte[] Key;
                internal int Size;
                internal float PixelSize;
            }

            // Both hooks attached; asked only once every mod has patched.
            internal static bool Active {
                get {
                    if (_active == null) {
                        _active = Generate != null
                            && PatchHelper.HasHook(Generate, typeof(GenerateHook))
                            && PatchHelper.HasHook(
                                AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.GenerateWorldMap)),
                                typeof(UploadHook));
                    }

                    return _active.Value;
                }
            }

            // Generate and DoFakeGenerate are looked up by name and type. If either is missing its maps are not cached,
            // and its own GenerateWorldMap prefix makes this fix stand down.
            internal static bool Resolve() {
                if (_resolved) { return Generate != null; }

                _resolved = true;
                if (!Chainloader.PluginInfos.TryGetValue(ExpandWorldSizeGuid, out PluginInfo info) || info.Instance == null) {
                    return false;
                }

                Type type = info.Instance.GetType().Assembly.GetType("ExpandWorldSize.MapGeneration");
                MethodInfo generate = type == null ? null : AccessTools.DeclaredMethod(type, "Generate", GenerateParameters);
                FieldInfo fake = type == null ? null : AccessTools.DeclaredField(type, "DoFakeGenerate");
                if (generate == null || !generate.IsStatic || generate.ReturnType != typeof(Task)
                    || fake == null || !fake.IsStatic || fake.FieldType != typeof(bool)) {
                    Logger.LogInfo($"{FixName}: this Expand World Size version generates the map in a way this fix " +
                                   "does not know, so its maps are not cached.");
                    return false;
                }

                Generate = generate;
                _fakeGenerate = fake;
                return true;
            }

            [HarmonyPatch]
            internal static class GenerateHook {
                private static bool Prepare() => Resolve();

                private static MethodBase TargetMethod() => Generate;

                // Arguments by position: (map, textureSize, pixelSize, biomePixels, maskPixels,
                // heightPixels, heightArray, cancellation).
                private static bool Prefix(Minimap __0, int __1, float __2, Color32[] __3, Color32[] __4,
                    Color[] __5, float[] __6, ref Task __result) {
                    _pending = null;
                    if (!Applies()) { return true; }

                    Stopwatch watch = Stopwatch.StartNew();
                    Texture2D[] textures = Textures(__0);
                    int[] formats = Formats(textures);
                    PixelDecoder decoder = PixelDecoder.For(formats);
                    if (decoder == null) { return true; }

                    byte[] key;
                    string path;
                    byte[] file;
                    try {
                        key = Key(__0, __1, __2, textures);
                        path = PathFor(key);
                        file = File.Exists(path) ? File.ReadAllBytes(path) : null;
                    } catch (Exception ex) {
                        WarnOnce($"the cached map could not be read, so it is generated. {ex.GetType().Name}: {ex.Message}");
                        return true;
                    }

                    // A file that is cut short or for another map is replaced by this miss's save.
                    int[] lengths = null;
                    if (file != null) {
                        try {
                            using (MemoryStream stream = new MemoryStream(file, writable: false)) {
                                lengths = ReadHeader(stream, key, formats, __1);
                            }
                        } catch (EndOfStreamException) {
                            lengths = null;
                        }

                        if (lengths != null && !decoder.Fits(lengths, __1)) { lengths = null; }
                    }

                    if (lengths == null) {
                        if (file == null) { LogMiss(); } else { LogMismatch(); }
                        _pending = new PendingSave { Key = key, Size = __1, PixelSize = __2 };
                        return true;
                    }

                    try {
                        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                    } catch (Exception) {
                        // Only decides which files trimming keeps.
                    }

                    Color32[] biome = __3, mask = __4;
                    Color[] heightColors = __5;
                    float[] heights = __6;
                    __result = Task.Run(() => Fill(file, lengths, decoder, biome, mask, heightColors, heights, path, watch));
                    return false;
                }
            }

            // After uploading its pixel arrays over a few frames, Expand World Size calls GenerateWorldMap once
            // with DoFakeGenerate set so other mods' postfixes run. Priority.First and HarmonyBefore: the flag is
            // read before its prefix clears it.
            [HarmonyPatch(typeof(Minimap), nameof(Minimap.GenerateWorldMap))]
            internal static class UploadHook {
                private static bool Prepare() => Resolve();

                [HarmonyPriority(Priority.First)]
                [HarmonyBefore(ExpandWorldSizeGuid)]
                private static void Prefix(Minimap __instance) {
                    PendingSave pending = _pending;
                    if (pending == null || !(bool)_fakeGenerate.GetValue(null)) { return; }

                    _pending = null;
                    try {
                        Texture2D[] textures = Textures(__instance);
                        if (__instance.m_textureSize != pending.Size || __instance.m_pixelSize != pending.PixelSize
                            || Array.Exists(textures, texture => texture.width != pending.Size)) {
                            return;
                        }

                        Save(textures, pending.Key);
                    } catch (Exception ex) {
                        WarnOnce($"the map could not be saved for the next join. {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            // Worker thread. A failure deletes the file and has the map generated again, once this
            // task's failure has stopped Expand World Size from uploading what it holds.
            private static void Fill(byte[] file, int[] lengths, PixelDecoder decoder, Color32[] biome, Color32[] mask,
                Color[] heightColors, float[] heights, string path, Stopwatch watch) {
                try {
                    byte[][] data;
                    using (MemoryStream stream = new MemoryStream(file, writable: false)) {
                        stream.Position = HeaderLength(lengths.Length);
                        data = ReadPayload(stream, lengths);
                    }

                    decoder.Decode(data, biome, mask, heightColors, heights);
                } catch (Exception ex) {
                    WarnOnce($"the cached map could not be loaded, so it is generated again. {ex.GetType().Name}: {ex.Message}");
                    TryDelete(path);
                    ThreadingHelper.Instance.StartSyncInvoke(() => {
                        if (Minimap.instance != null) { Minimap.instance.GenerateWorldMap(); }
                    });
                    throw;
                }

                LogHit(watch);
            }

            // Magic, version, a SHA-256 key and the texture count, then four ints per texture.
            private static int HeaderLength(int textures) => 4 + 4 + 32 + 4 + textures * 16;
        }

        // Turns raw texture data back into the pixel arrays Expand World Size uploads, with each format's channel
        // layout read from a texture Unity writes; a 4-bit channel n becomes 17n, which Unity writes back as n.
        private sealed class PixelDecoder {
            private Channel[] _biome;
            private Channel[] _mask;
            private int _biomeBytes;
            private int _maskBytes;
            private float[] _halfToFloat;

            // Decided once per set of formats; null when this device's formats cannot be decoded.
            private static int[] _builtFor;
            private static PixelDecoder _built;

            private struct Channel {
                internal int Shift;
                internal int Bits;
            }

            internal static PixelDecoder For(int[] formats) {
                if (_builtFor != null && SameFormats(formats, _builtFor)) { return _built; }

                _builtFor = formats;
                _built = Build(formats);
                if (_built == null) {
                    Logger.LogInfo($"{FixName}: the map's texture formats on this graphics device are not ones this fix " +
                                   "can read back, so Expand World Size maps are not cached.");
                }

                return _built;
            }

            private static bool SameFormats(int[] a, int[] b) {
                if (a.Length != b.Length) { return false; }
                for (int i = 0; i < a.Length; i++) {
                    if (a[i] != b[i]) { return false; }
                }

                return true;
            }

            private static PixelDecoder Build(int[] formats) {
                if ((GraphicsFormat)formats[2] != GraphicsFormat.R16_SFloat) { return null; }

                PixelDecoder decoder = new PixelDecoder();
                decoder._biome = Layout((GraphicsFormat)formats[0], out decoder._biomeBytes);
                decoder._mask = Layout((GraphicsFormat)formats[1], out decoder._maskBytes);
                if (decoder._biome == null || decoder._mask == null) { return null; }

                decoder._halfToFloat = new float[65536];
                for (int h = 0; h < decoder._halfToFloat.Length; h++) { decoder._halfToFloat[h] = Mathf.HalfToFloat((ushort)h); }

                return decoder;
            }

            internal bool Fits(int[] lengths, int size) {
                long pixels = (long)size * size;
                return lengths[0] == pixels * _biomeBytes && lengths[1] == pixels * _maskBytes && lengths[2] == pixels * 2;
            }

            // Made the way the game made its own: a format the device cannot sample, such as the
            // biome map's RGB24, exists only through its TextureFormat.
            private static Texture2D Create(GraphicsFormat format) {
                if (SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Sample)) {
                    return new Texture2D(4, 1, format, TextureCreationFlags.None);
                }

                TextureFormat textureFormat = GraphicsFormatUtility.GetTextureFormat(format);
                return (int)textureFormat == 0
                    ? null
                    : new Texture2D(4, 1, textureFormat, false, !GraphicsFormatUtility.IsSRGBFormat(format));
            }

            // Main thread: one pixel per channel, that channel full and the rest empty.
            private static Channel[] Layout(GraphicsFormat format, out int bytesPerPixel) {
                bytesPerPixel = 0;
                Texture2D probe = Create(format);
                if (probe == null) { return null; }

                try {
                    if (probe.graphicsFormat != format) { return null; }

                    probe.SetPixels32(new[] {
                        new Color32(255, 0, 0, 0), new Color32(0, 255, 0, 0), new Color32(0, 0, 255, 0), new Color32(0, 0, 0, 255),
                    });
                    byte[] raw = probe.GetRawTextureData();
                    bytesPerPixel = raw.Length / 4;
                    if (raw.Length % 4 != 0 || bytesPerPixel < 1 || bytesPerPixel > 4) { return null; }

                    Channel[] channels = new Channel[4];
                    for (int c = 0; c < 4; c++) {
                        uint bits = Read(raw, c * bytesPerPixel, bytesPerPixel);
                        if (bits == 0) {
                            // A format without this channel; alpha alone may be missing.
                            if (c != 3) { return null; }
                            channels[c] = new Channel { Bits = 0 };
                            continue;
                        }

                        int shift = 0;
                        while ((bits & 1) == 0) { bits >>= 1; shift++; }
                        int width = 0;
                        while ((bits & 1) == 1) { bits >>= 1; width++; }
                        if (bits != 0 || (width != 4 && width != 8)) { return null; }

                        channels[c] = new Channel { Shift = shift, Bits = width };
                    }

                    return channels;
                } finally {
                    UnityEngine.Object.Destroy(probe);
                }
            }

            private static uint Read(byte[] data, int offset, int count) {
                uint value = 0;
                for (int i = 0; i < count; i++) { value |= (uint)data[offset + i] << (8 * i); }
                return value;
            }

            private static byte Expand(uint value, Channel channel) {
                if (channel.Bits == 0) { return 255; }

                uint n = (value >> channel.Shift) & ((1u << channel.Bits) - 1);
                return (byte)(channel.Bits == 4 ? n * 17 : n);
            }

            internal void Decode(byte[][] data, Color32[] biome, Color32[] mask, Color[] heightColors, float[] heights) {
                byte[] biomeData = data[0], maskData = data[1], heightData = data[2];
                for (int i = 0; i < biome.Length; i++) {
                    uint b = Read(biomeData, i * _biomeBytes, _biomeBytes);
                    biome[i] = new Color32(Expand(b, _biome[0]), Expand(b, _biome[1]), Expand(b, _biome[2]), Expand(b, _biome[3]));

                    uint m = Read(maskData, i * _maskBytes, _maskBytes);
                    mask[i] = new Color32(Expand(m, _mask[0]), Expand(m, _mask[1]), Expand(m, _mask[2]), Expand(m, _mask[3]));

                    float height = _halfToFloat[heightData[2 * i] | (heightData[2 * i + 1] << 8)];
                    heights[i] = height;
                    heightColors[i] = new Color(height, 0f, 0f);
                }
            }
        }
    }
}
