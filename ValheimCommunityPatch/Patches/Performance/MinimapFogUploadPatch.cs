using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace ValheimCommunityPatch.Patches.Performance {
    // Fix Minimap Fog Upload: revealing new ground uploads only the patch of map fog around the
    // player instead of the whole fog texture.
    //
    // Every two seconds Minimap.Explore clears the fog in a small circle around the player and, if
    // any pixel was newly revealed, calls Texture2D.Apply on the fog texture: 2048 x 2048 two-byte
    // pixels, 8 MiB sent to the graphics card for a circle a couple of dozen pixels across. While
    // exploring that is a periodic hitch on the main and render threads.
    //
    // A transpiler replaces that one Apply call. The replacement copies the square vanilla just
    // scanned from the texture's CPU data into a small staging texture of the same format, uploads
    // the stage, and copies it into place on the graphics card. The CPU data stays the source of
    // truth: Explore wrote it with SetPixel as before, and every other full Apply (map load, map
    // table, console commands) sends all of it again, so the two never disagree. Only this explore
    // path is changed, and like vanilla it uploads nothing when nothing new was revealed; a mod that
    // writes fog pixels without calling Apply itself was never shown promptly by vanilla either.
    //
    // A square too large for the stage gets vanilla's full Apply. A texture the copy cannot serve
    // (no texture copy support, mipmaps, unreadable, unexpected size or format) gets vanilla for
    // the session, and any failure falls back to a full Apply and stands the fix down, each said
    // once in the log.
    //
    // Client: the minimap exists only where there is a local player.
    [PatchSide(Side.Client)]
    [ModDisableable]
    [HarmonyPatch(typeof(Minimap), "Explore", typeof(Vector3), typeof(float))]
    internal static class MinimapFogUploadPatch {
        private const string FixName = "Fix Minimap Fog Upload";

        internal static ConfigEntry<bool> Verify;

        internal static void BindConfig() {
            Verify = ValConfig.BindServerConfig(
                ValConfig.SectionDebug,
                "Verify Minimap Fog Upload",
                false,
                "Diagnostic. Reads each partial map fog upload back from the graphics card, compares " +
                "it with what was sent, and logs any difference. Leave it off unless the map fog " +
                "looks wrong.",
                advanced: true);
        }

        // The explore square is 2 * ceil(radius / pixelSize) + 1 pixels; vanilla's is about 19.
        private const int MaxStage = 256;

        private static readonly MethodInfo ApplyMethod =
            AccessTools.Method(typeof(Texture2D), nameof(Texture2D.Apply), Type.EmptyTypes);

        private static readonly MethodInfo UploadMethod =
            AccessTools.Method(typeof(MinimapFogUploadPatch), nameof(UploadExplored));

        private static Texture2D _stage;
        private static bool _failed;

        private static readonly FixSwitch ApiSwitch = FixRegistry.SwitchOf(typeof(MinimapFogUploadPatch));

        // Whether a fog texture can be served, decided once per texture: a new world makes a new one.
        private static Texture2D _checkedFog;
        private static bool _usable;
        private static int _bytesPerPixel;

        // Priority.Last: see ValheimCommunityPatch.ApplyPatches.
        [HarmonyTranspiler]
        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = PatchHelper.Copy(instructions);

            int replaced = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (!codes[i].Calls(ApplyMethod)) { continue; }

                // Stack [fog] -> [fog, this, p, radius] -> UploadExplored(fog, this, p, radius).
                // The Apply instruction becomes the first load, so its labels stay put.
                codes[i].opcode = OpCodes.Ldarg_0;
                codes[i].operand = null;
                codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_1));
                codes.Insert(i + 2, new CodeInstruction(OpCodes.Ldarg_2));
                codes.Insert(i + 3, new CodeInstruction(OpCodes.Call, UploadMethod));
                replaced++;
                i += 3;
            }

            if (replaced != 1) {
                Logger.LogWarning(
                    $"Minimap.Explore: expected 1 fog texture Apply call, found {replaced}, so this fix is " +
                    "inactive. Another mod has most likely already rewritten the method - if so, " +
                    "nothing is wrong.");
                return instructions;
            }

            return codes;
        }

        private static void UploadExplored(Texture2D fog, Minimap map, Vector3 p, float radius) {
            if (_failed || ApiSwitch.Off || !Usable(fog, map)) {
                fog.Apply();
                return;
            }

            try {
                // The square vanilla's loop scanned, which holds every pixel it can have revealed.
                int reach = (int)Mathf.Ceil(radius / map.m_pixelSize);
                map.WorldToPixel(p, out int px, out int py);

                int size = map.m_textureSize;
                int x0 = Math.Max(px - reach, 0), x1 = Math.Min(px + reach, size - 1);
                int y0 = Math.Max(py - reach, 0), y1 = Math.Min(py + reach, size - 1);
                int width = x1 - x0 + 1, height = y1 - y0 + 1;

                if (reach < 0 || width <= 0 || height <= 0 || width > MaxStage || height > MaxStage) {
                    fog.Apply();
                    return;
                }

                if (!EnsureStage(fog, Math.Max(width, height))) {
                    fog.Apply();
                    return;
                }

                NativeArray<byte> source = fog.GetPixelData<byte>(0);
                NativeArray<byte> stage = _stage.GetPixelData<byte>(0);
                int rowBytes = width * _bytesPerPixel;
                for (int row = 0; row < height; row++) {
                    NativeArray<byte>.Copy(
                        source, ((y0 + row) * size + x0) * _bytesPerPixel,
                        stage, row * _stage.width * _bytesPerPixel,
                        rowBytes);
                }

                _stage.Apply(false, false);
                Graphics.CopyTexture(_stage, 0, 0, 0, 0, width, height, fog, 0, 0, x0, y0);

                if (Verify != null && Verify.Value) { RequestCheck(fog, source, size, x0, y0, width, height); }
            } catch (Exception ex) {
                _failed = true;
                Logger.LogWarning(
                    $"{FixName}: a partial map fog upload failed, so the game's full upload is used for " +
                    $"the rest of the session. {ex.GetType().Name}: {ex.Message}");
                fog.Apply();
            }
        }

        private static bool Usable(Texture2D fog, Minimap map) {
            if (ReferenceEquals(fog, _checkedFog)) { return _usable; }

            _checkedFog = fog;
            _usable = false;

            string reason = null;
            int size = map.m_textureSize;
            if ((SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) == 0) {
                reason = "this graphics device cannot copy between textures";
            } else if (fog.mipmapCount != 1) {
                reason = "the map fog texture has mipmaps";
            } else if (!fog.isReadable) {
                reason = "the map fog texture is not readable";
            } else if (fog.width != size || fog.height != size) {
                reason = "the map fog texture is not the map's size";
            } else {
                int bytes = fog.GetPixelData<byte>(0).Length;
                int pixels = size * size;
                if (pixels <= 0 || bytes % pixels != 0 || bytes / pixels <= 0) {
                    reason = "the map fog texture's format is not one whole number of bytes per pixel";
                } else {
                    _bytesPerPixel = bytes / pixels;
                }
            }

            if (reason != null) {
                Logger.LogInfo($"{FixName}: {reason}, so the game's full upload is used for this map.");
                return false;
            }

            _usable = true;
            return true;
        }

        // Grown to the next power of two that fits, never shrunk; recreated if the format changes.
        private static bool EnsureStage(Texture2D fog, int needed) {
            if (_stage != null && _stage.graphicsFormat == fog.graphicsFormat && _stage.width >= needed) { return true; }

            int stageSize = 16;
            while (stageSize < needed) { stageSize *= 2; }

            if (_stage != null) { UnityEngine.Object.Destroy(_stage); }

            _stage = new Texture2D(stageSize, stageSize, fog.graphicsFormat, TextureCreationFlags.None) {
                name = "VCP map fog stage",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            if (_stage.graphicsFormat == fog.graphicsFormat && _stage.mipmapCount == 1) { return true; }

            _failed = true;
            Logger.LogInfo($"{FixName}: a staging texture in the map fog's format is not available, so the " +
                           "game's full upload is used for the rest of the session.");
            return false;
        }

        // ---- Verify ---------------------------------------------------------------------------

        private static int _checked;
        private static int _diverged;

        private static void RequestCheck(
            Texture2D fog, NativeArray<byte> source, int size, int x0, int y0, int width, int height) {
            if (!SystemInfo.supportsAsyncGPUReadback) { return; }

            int rowBytes = width * _bytesPerPixel;
            byte[] expected = new byte[rowBytes * height];
            for (int row = 0; row < height; row++) {
                NativeArray<byte>.Copy(source, ((y0 + row) * size + x0) * _bytesPerPixel, expected, row * rowBytes, rowBytes);
            }

            AsyncGPUReadback.Request(fog, 0, x0, width, y0, height, 0, 1, request => Compare(request, expected, x0, y0));
        }

        private static void Compare(AsyncGPUReadbackRequest request, byte[] expected, int x0, int y0) {
            if (request.hasError) { return; }

            NativeArray<byte> actual = request.GetData<byte>();
            int mismatched = 0;
            int length = Math.Min(actual.Length, expected.Length);
            for (int i = 0; i < length; i++) {
                if (actual[i] != expected[i]) { mismatched++; }
            }

            if (actual.Length != expected.Length) { mismatched += Math.Abs(actual.Length - expected.Length); }

            _checked++;
            if (mismatched > 0) {
                _diverged++;
                Logger.LogWarning(
                    $"Map fog verify: the upload at ({x0}, {y0}) differs from the map's data in {mismatched} " +
                    "byte(s). Please report this.");
            }

            if (_checked % 50 == 0) {
                Logger.LogInfo($"Map fog verify: {_checked} upload(s) checked, {_diverged} differed.");
            }
        }
    }
}
