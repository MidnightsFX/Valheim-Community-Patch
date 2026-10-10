using System;
using System.Collections.Generic;
using BepInEx.Configuration;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>
    /// What each fix ended up doing this session, for the mod API. A fix is a top-level patch
    /// class under ValheimCommunityPatch.Patches, and its id is the class name.
    /// </summary>
    /// <remarks>
    /// Fed from three places: ApplyPatches records whether each class patched, BindFixToggle
    /// records the fix's on/off entry, and the stand-down checks record when a fix gives way to
    /// another mod. Stand-downs are mostly decided lazily, at a fix's first use in a world, so a
    /// fix can read Active early in a session and StoodDown later. Read from any thread.
    /// </remarks>
    internal static class FixRegistry {
        // The values the API file documents. Never renumber: other mods compare against these.
        internal const int StateUnknown = 0;
        internal const int StateActive = 1;
        internal const int StateDisabled = 2;
        internal const int StateStoodDown = 3;
        internal const int StateNotApplied = 4;
        internal const int StateFailed = 5;

        private enum Outcome { Pending, Applied, NotApplied, Failed }

        private sealed class Entry {
            internal Outcome Outcome;
            internal ConfigEntry<bool> Toggle;
            internal volatile bool StoodDown;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> ById = new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>The fix a patch class belongs to: its outermost declaring class, if that is a fix.</summary>
        internal static Type FixOf(Type type) {
            while (type?.DeclaringType != null) { type = type.DeclaringType; }

            return type?.Namespace != null && type.Namespace.StartsWith("ValheimCommunityPatch.Patches", StringComparison.Ordinal)
                ? type
                : null;
        }

        /// <summary>
        /// Records how applying one patch class went. A fix whose nested hook class failed is
        /// Failed even when its other classes applied.
        /// </summary>
        internal static void RecordPatched(Type patchClass, bool patchedAnything) {
            Entry entry = Get(patchClass);
            if (entry == null) { return; }

            lock (Gate) {
                if (entry.Outcome == Outcome.Failed) { return; }
                if (patchedAnything) { entry.Outcome = Outcome.Applied; }
                else if (entry.Outcome == Outcome.Pending) { entry.Outcome = Outcome.NotApplied; }
            }
        }

        internal static void RecordFailed(Type patchClass) {
            Entry entry = Get(patchClass);
            if (entry == null) { return; }

            lock (Gate) { entry.Outcome = Outcome.Failed; }
        }

        /// <summary>Records a patch class that was deliberately not applied (client-only on a dedicated server).</summary>
        internal static void RecordSkipped(Type patchClass) => RecordPatched(patchClass, patchedAnything: false);

        internal static void RecordToggle(Type patchClass, ConfigEntry<bool> toggle) {
            Entry entry = Get(patchClass);
            if (entry == null) { return; }

            lock (Gate) { entry.Toggle = toggle; }
        }

        /// <summary>Records that a fix has given way to another mod, or found its own hooks missing, for the rest of the session.</summary>
        internal static void MarkStoodDown(Type patchClass) {
            Entry entry = Get(patchClass);
            if (entry != null) { entry.StoodDown = true; }
        }

        internal static List<string> GetFixIds() {
            lock (Gate) {
                List<string> ids = new List<string>(ById.Keys);
                ids.Sort(StringComparer.Ordinal);
                return ids;
            }
        }

        internal static int GetState(string fixId) {
            if (string.IsNullOrEmpty(fixId)) { return StateUnknown; }

            lock (Gate) {
                if (!ById.TryGetValue(fixId, out Entry entry)) { return StateUnknown; }

                switch (entry.Outcome) {
                    case Outcome.Failed: return StateFailed;
                    case Outcome.Pending:
                    case Outcome.NotApplied: return StateNotApplied;
                }

                if (entry.Toggle != null && !entry.Toggle.Value) { return StateDisabled; }

                return entry.StoodDown ? StateStoodDown : StateActive;
            }
        }

        private static Entry Get(Type patchClass) {
            Type fix = FixOf(patchClass);
            if (fix == null) { return null; }

            lock (Gate) {
                if (!ById.TryGetValue(fix.Name, out Entry entry)) {
                    entry = new Entry();
                    ById.Add(fix.Name, entry);
                }

                return entry;
            }
        }
    }
}
