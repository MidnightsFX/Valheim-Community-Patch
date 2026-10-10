using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Configuration;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>
    /// What each fix ended up doing this session, for the mod API. A fix is a top-level patch
    /// class under ValheimCommunityPatch.Patches, and its id is the class name.
    /// </summary>
    /// <remarks>
    /// Fed from four places: ApplyPatches records whether each class patched, BindFixToggle
    /// records the fix's on/off entry, the stand-down checks record when a fix gives way to
    /// another mod, and <see cref="Disable"/> records another mod turning a fix off through the
    /// API. Stand-downs are mostly decided lazily, at a fix's first use in a world, so a fix can
    /// read Active early in a session and StoodDown later. Read from any thread.
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
            internal Type Fix;
            internal Outcome Outcome;
            internal ConfigEntry<bool> Toggle;
            internal volatile bool StoodDown;
            internal readonly FixSwitch Switch = new FixSwitch();
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> ById = new Dictionary<string, Entry>(StringComparer.Ordinal);

        // Requests already warned about, so a mod that asks every frame logs once.
        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

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

        /// <summary>
        /// The switch another mod turns a fix off with. A fix with a config toggle reads it through
        /// its <see cref="FixToggle"/>; one without holds it in a static field and checks it where
        /// it decides to act. Never null, so it can be held from a field initializer.
        /// </summary>
        internal static FixSwitch SwitchOf(Type patchClass) => Get(patchClass)?.Switch ?? FixSwitch.Inert;

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

                return entry.StoodDown || entry.Switch.Off ? StateStoodDown : StateActive;
            }
        }

        /// <summary>True when the fix is marked <see cref="ModDisableableAttribute"/>.</summary>
        internal static bool CanDisable(string fixId) {
            if (string.IsNullOrEmpty(fixId)) { return false; }

            lock (Gate) {
                return ById.TryGetValue(fixId, out Entry entry) && IsDisableable(entry.Fix);
            }
        }

        /// <summary>
        /// Another mod's request to turn a fix off for the rest of the session. Logged as a
        /// warning naming the caller, whether or not it is granted, since it changes what this
        /// mod does without the server admin's say. Main thread: a fix may tidy up on turn-off.
        /// </summary>
        /// <param name="caller">Who asked, as the API receiver worked it out from the call stack.</param>
        /// <returns>
        /// True when the fix is off for the rest of the session: turned off now or earlier, never
        /// applied this session, or stood down already. False for an unknown id, a fix that cannot
        /// be turned off while the game runs, or a call from another thread.
        /// </returns>
        internal static bool Disable(string fixId, string reason, string caller) {
            string asked = string.IsNullOrEmpty(reason) ? "" : $" Reason given: \"{reason}\".";

            // Turning off can wake objects and release queued work, which is Unity work.
            if (Thread.CurrentThread.ManagedThreadId != ValheimCommunityPatch.MainThreadId) {
                WarnOnce(fixId + "|" + caller + "|thread",
                    $"{caller} asked to turn off fix '{fixId}' from a background thread, so nothing changed. " +
                    $"Ask from the main thread.{asked}");
                return false;
            }

            Entry entry = null;
            if (!string.IsNullOrEmpty(fixId)) {
                lock (Gate) { ById.TryGetValue(fixId, out entry); }
            }

            if (entry == null) {
                WarnOnce(fixId + "|" + caller,
                    $"{caller} asked to turn off fix '{fixId}', but no fix has that id, so nothing changed.{asked}");
                return false;
            }

            string name = Describe(fixId, entry);
            int state = GetState(fixId);
            bool notRunning = state == StateNotApplied || state == StateFailed;

            if (!IsDisableable(entry.Fix)) {
                bool alreadyOff = notRunning || state == StateStoodDown;
                WarnOnce(fixId + "|" + caller,
                    $"{caller} asked to turn off {name}, but that fix cannot be turned off while the game is running" +
                    (alreadyOff ? "; it is not running this session anyway." : ", so it stays as it is.") +
                    asked);
                return alreadyOff;
            }

            if (entry.Switch.Off) {
                WarnOnce(fixId + "|" + caller, $"{caller} asked to turn off {name}, which was already turned off.{asked}");
                return true;
            }

            // Turned off even when it is not running, so the answer cannot change later.
            WarnOnce(fixId + "|" + caller,
                notRunning
                    ? $"{caller} turned off {name} for the rest of this session; it was not running this session anyway.{asked}"
                    : $"{name} was turned off for the rest of this session by {caller}. The game's own " +
                      $"behaviour applies in its place; restart the game to have this fix back.{asked}");
            entry.Switch.TurnOff();
            return true;
        }

        private static bool IsDisableable(Type fix) =>
            fix != null && fix.IsDefined(typeof(ModDisableableAttribute), false);

        // The config key admins know the fix by, where it has one, with the id other mods use.
        private static string Describe(string fixId, Entry entry) {
            ConfigEntry<bool> toggle = entry.Toggle;
            return toggle != null ? $"'{toggle.Definition.Key}' ({fixId})" : $"fix {fixId}";
        }

        private static void WarnOnce(string key, string message) {
            bool first;
            lock (Gate) { first = Warned.Add(key); }

            if (first) { Logger.LogWarning(message); }
            else { Logger.LogDebug(message); }
        }

        private static Entry Get(Type patchClass) {
            Type fix = FixOf(patchClass);
            if (fix == null) { return null; }

            lock (Gate) {
                if (!ById.TryGetValue(fix.Name, out Entry entry)) {
                    entry = new Entry { Fix = fix };
                    ById.Add(fix.Name, entry);
                }

                return entry;
            }
        }
    }

    /// <summary>
    /// One fix's off switch for the mod API. Once off it stays off for the session: a fix that
    /// handed its work back to the game mid-session cannot be trusted to pick it up again, since
    /// its caches and indexes stopped following the game while it was off.
    /// </summary>
    internal sealed class FixSwitch {
        /// <summary>For code that is not part of a fix; never turned off.</summary>
        internal static readonly FixSwitch Inert = new FixSwitch();

        private volatile bool _off;

        internal bool Off => _off;

        /// <summary>
        /// Raised once, on the thread that turned the fix off (the main thread, by the API's
        /// contract), for a fix that has work to hand back: queued items to release, objects to
        /// wake. A handler that throws is logged and does not stop the others.
        /// </summary>
        internal event Action TurnedOff;

        internal void TurnOff() {
            if (_off) { return; }

            _off = true;

            Action handlers = TurnedOff;
            if (handlers == null) { return; }

            foreach (Action handler in handlers.GetInvocationList()) {
                try {
                    handler();
                } catch (Exception ex) {
                    Logger.LogError($"Turning a fix off did not finish tidying up: {ex}");
                }
            }
        }
    }

    /// <summary>
    /// Marks a fix that another mod may turn off through the API while the game runs: every
    /// change it makes is behind a gate that reads its <see cref="FixSwitch"/> (through its
    /// <see cref="FixToggle"/>, or an ApiSwitch field), and turning it off mid-session hands
    /// everything back to the game cleanly, releasing anything it still holds. Add it only after
    /// checking both, and list a fix that cannot have it in the API documentation.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    internal sealed class ModDisableableAttribute : Attribute { }
}
