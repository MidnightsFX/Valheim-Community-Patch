using BepInEx.Logging;
using System;


#pragma warning disable IDE0130
namespace JotunnModStub {
#pragma warning restore IDE0130
    internal class Logger {
        public static LogLevel Level = LogLevel.Info;

        public static void EnableDebugLogging(object sender, EventArgs e) {
            CheckEnableDebugLogging();
        }

        public static void CheckEnableDebugLogging() {
            if (ValConfig.EnableDebugMode.Value) {
                Level = LogLevel.Debug;
            } else {
                Level = LogLevel.Info;
            }
        }

        public static void SetDebugLogging(bool state) {
            if (state) {
                Level = LogLevel.Debug;
            } else {
                Level = LogLevel.Info;
            }
        }

        public static void LogDebug(string message) {
            if (Level >= LogLevel.Debug) {
                JotunnModStub.Log.LogInfo("[DEBUG]" + message);
            }
        }
        public static void LogInfo(string message) {
            if (Level >= LogLevel.Info) {
                JotunnModStub.Log.LogInfo(message);
            }
        }

        public static void LogWarning(string message) {
            if (Level >= LogLevel.Warning) {
                JotunnModStub.Log.LogWarning(message);
            }
        }

        public static void LogError(string message) {
            if (Level >= LogLevel.Error) {
                JotunnModStub.Log.LogError(message);
            }
        }
    }
}
