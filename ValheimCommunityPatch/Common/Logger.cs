#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>The mod's log, with a debug channel switched by the EnableDebugMode config entry.</summary>
    internal static class Logger {
        private static bool _debug;

        internal static bool DebugEnabled => _debug;

        internal static void SetDebug(bool enabled) => _debug = enabled;

        // Debug lines go out at Info level with a prefix, because BepInEx hides its own Debug
        // level from the console by default.
        internal static void LogDebug(string message) {
            if (_debug) { ValheimCommunityPatch.Log.LogInfo("[DEBUG]" + message); }
        }

        internal static void LogInfo(string message) => ValheimCommunityPatch.Log.LogInfo(message);

        internal static void LogWarning(string message) => ValheimCommunityPatch.Log.LogWarning(message);

        internal static void LogError(string message) => ValheimCommunityPatch.Log.LogError(message);

        /// <summary>
        /// A drop-in for ZLog.Log(object) that only writes when debug logging is on. The log-spam
        /// fixes point vanilla's calls here, so the messages are silenced by default but still
        /// recoverable.
        /// </summary>
        internal static void DebugSink(object message) {
            if (_debug) { LogDebug(message?.ToString() ?? string.Empty); }
        }
    }
}
