using System;
using System.IO;
using System.IO.Compression;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimCommunityPatch.Patches.Performance {
    // Vanilla defect: Minimap.GenerateWorldMap recomputes the whole world's forest mask, map and
    // height textures from WorldGenerator on every login - a fixed multi-second block inside the
    // load screen (a visible slice of the 15-20 s login stalls in every profiled session), for
    // textures that are a pure function of the world seed.
    //
    // Fix: cache the three raw textures to disk, GZip-compressed, keyed by world name + seed +
    // game version - a game update or a different world regenerates automatically, and the map
    // FOG (exploration) is untouched because it lives in the per-character save, not here. Any
    // read failure deletes the file and falls through to vanilla generation, so a corrupt or
    // truncated cache costs one regeneration, never a broken map.
    //
    // Provenance: ontrigger's ValheimPerformanceOptimizations (MIT), plus the corruption
    // handling.
    //
    // Client: dedicated servers never build the minimap.
    [PatchSide(Side.Client)]
    [HarmonyPatch(typeof(Minimap))]
    internal static class MinimapCachePatch {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig() {
            Enabled = ValConfig.BindFixToggle(
                typeof(MinimapCachePatch),
                ValConfig.SectionPerformance,
                "Fix Map Generation Stall",
                true,
                "Caches the generated world-map textures to disk per world seed and game " +
                "version, so logins after the first skip the multi-second map computation " +
                "inside the load screen. Exploration fog is unaffected - it lives in the " +
                "character save.");
        }

        [HarmonyPrefix]
        [HarmonyPatch("GenerateWorldMap")]
        private static bool GenerateWorldMapPrefix(Minimap __instance) {
            if (Enabled == null || !Enabled.Value) { return true; }

            string path = CacheFilePath();
            if (path == null || !File.Exists(path)) { return true; }

            try {
                using (FileStream file = File.OpenRead(path))
                using (GZipStream unzip = new GZipStream(file, CompressionMode.Decompress))
                using (MemoryStream buffer = new MemoryStream()) {
                    unzip.CopyTo(buffer);
                    ZPackage package = new ZPackage(buffer.ToArray());

                    __instance.m_forestMaskTexture.LoadRawTextureData(package.ReadByteArray());
                    __instance.m_forestMaskTexture.Apply();
                    __instance.m_mapTexture.LoadRawTextureData(package.ReadByteArray());
                    __instance.m_mapTexture.Apply();
                    __instance.m_heightTexture.LoadRawTextureData(package.ReadByteArray());
                    __instance.m_heightTexture.Apply();
                }

                return false;
            } catch (Exception e) {
                // A corrupt or truncated cache must never cost more than one regeneration.
                Logger.LogWarning($"Map cache at '{path}' could not be read ({e.GetType().Name}); regenerating.");
                try { File.Delete(path); } catch (Exception) { }
                return true;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch("GenerateWorldMap")]
        private static void GenerateWorldMapPostfix(Minimap __instance) {
            if (Enabled == null || !Enabled.Value) { return; }

            string path = CacheFilePath();
            if (path == null || File.Exists(path)) { return; }

            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                ZPackage package = new ZPackage();
                package.Write(__instance.m_forestMaskTexture.GetRawTextureData());
                package.Write(__instance.m_mapTexture.GetRawTextureData());
                package.Write(__instance.m_heightTexture.GetRawTextureData());
                byte[] data = package.GetArray();

                using (FileStream file = File.Create(path))
                using (GZipStream zip = new GZipStream(file, CompressionMode.Compress)) {
                    zip.Write(data, 0, data.Length);
                }
            } catch (Exception e) {
                // Failing to WRITE the cache is only a lost optimization, never an error.
                Logger.LogWarning($"Map cache could not be written ({e.GetType().Name}); logins will keep regenerating.");
            }
        }

        // Keyed by world identity AND game version: a game update that changes worldgen output
        // silently invalidates every cache. Terrain-modifying mods are not modeled - the map is
        // biome/height-level, which those mods do not change from vanilla's generator.
        private static string CacheFilePath() {
            if (ZNet.m_world == null) { return null; }

            string version = Version.GetVersionString().Replace("/", "_");
            return Path.Combine(
                World.GetWorldSavePath(ZNet.m_world.m_fileSource), "minimap",
                $"{ZNet.m_world.m_name}_{ZNet.m_world.m_seed}_{version}.map");
        }
    }
}
