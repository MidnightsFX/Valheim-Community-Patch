using System;
using BepInEx.Configuration;

#pragma warning disable IDE0130
namespace ValheimCommunityPatch {
#pragma warning restore IDE0130

    /// <summary>
    /// A fix's on/off config entry together with its mod API switch, so another mod can turn the
    /// fix off for the session without touching the admin's config. Fixes read
    /// <see cref="Value"/> exactly where they used to read the entry's.
    /// </summary>
    internal sealed class FixToggle {
        private readonly ConfigEntry<bool> _entry;
        private readonly FixSwitch _switch;

        internal FixToggle(ConfigEntry<bool> entry, FixSwitch fixSwitch) {
            _entry = entry;
            _switch = fixSwitch;

            _entry.SettingChanged += (sender, args) => SettingChanged?.Invoke(this, args);
            _switch.TurnedOff += () => SettingChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>True when the config has the fix on and no mod has turned it off.</summary>
        internal bool Value => !_switch.Off && _entry.Value;

        /// <summary>The config entry alone, as the admin set it.</summary>
        internal ConfigEntry<bool> Entry => _entry;

        /// <summary>Raised when the config entry changes, or when a mod turns the fix off. Read <see cref="Value"/> for the outcome.</summary>
        internal event EventHandler SettingChanged;
    }
}
