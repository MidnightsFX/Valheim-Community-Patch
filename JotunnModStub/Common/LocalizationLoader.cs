using Jotunn.Entities;
using Jotunn.Managers;
using JotunnModStub;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace JotunnModStub.Common {
    internal static class LocalizationLoader {

        internal static string LocalizationFolder = "Localizations";

        // This loads all localizations within the localization directory.
        // Localizations should be plain JSON objects with each of the two required entries being separate eg:
        // "item_sword": "sword-name-here",
        // "item_sword_description": "sword-description-here",
        // the localization file itself should be a case matched language as defined by one of the "folder" language names from here:
        // https://valheim-modding.github.io/Jotunn/data/localization/language-list.html
        internal static void AddLocalizations() {
            CustomLocalization Localization = JotunnModStub.Localization;

            // Ensure localization folder exists
            var translationFolder = Path.Combine(BepInEx.Paths.ConfigPath, ValConfig.cfgFolder, LocalizationFolder);
            Directory.CreateDirectory(translationFolder);
            foreach (string embeddedResource in Assembly.GetExecutingAssembly().GetManifestResourceNames()) {
                if (!embeddedResource.Contains(LocalizationFolder)) { continue; }
                // Read the localization file

                string localization = ReadEmbeddedResourceFile(embeddedResource);
                // since I use comments in the localization that are not valid JSON those need to be stripped
                string cleaned_localization = Regex.Replace(localization, @"\/\/.*", "");
                Dictionary<string, string> internal_localization = SimpleJson.SimpleJson.DeserializeObject<Dictionary<string, string>>(cleaned_localization);
                // Just the localization name
                var localization_name = embeddedResource.Split('.');
                if (File.Exists($"{translationFolder}/{localization_name[2]}.json")) {
                    string cached_translation_file = File.ReadAllText($"{translationFolder}/{localization_name[2]}.json");
                    try {
                        Dictionary<string, string> cached_localization = SimpleJson.SimpleJson.DeserializeObject<Dictionary<string, string>>(cached_translation_file);
                        UpdateLocalizationWithMissingKeys(internal_localization, cached_localization);
                        Logger.LogDebug($"Reading {translationFolder}/{localization_name[2]}.json");
                        File.WriteAllText($"{translationFolder}/{localization_name[2]}.json", SimpleJson.SimpleJson.SerializeObject(cached_localization));
                        string updated_local_translation = File.ReadAllText($"{translationFolder}/{localization_name[2]}.json");
                        Localization.AddJsonFile(localization_name[2], updated_local_translation);
                    } catch {
                        File.WriteAllText($"{translationFolder}/{localization_name[2]}.json", cleaned_localization);
                        Logger.LogDebug($"Reading {embeddedResource}");
                        Localization.AddJsonFile(localization_name[2], cleaned_localization);
                    }
                } else {
                    File.WriteAllText($"{translationFolder}/{localization_name[2]}.json", cleaned_localization);
                    Logger.LogDebug($"Reading {embeddedResource}");
                    Localization.AddJsonFile(localization_name[2], cleaned_localization);
                }
                Logger.LogDebug($"Added localization: '{localization_name[2]}'");
            }
        }

        private static Dictionary<string, string> UpdateLocalizationWithMissingKeys(Dictionary<string, string> internal_localization, Dictionary<string, string> cached_localization) {
            if (internal_localization.Keys != cached_localization.Keys) {
                List<string> extra_keys = cached_localization.Keys.ToList();
                foreach (KeyValuePair<string, string> entry in internal_localization) {
                    extra_keys.Remove(entry.Key);
                    if (!cached_localization.ContainsKey(entry.Key)) {
                        Logger.LogDebug($"Adding missing localization key {entry.Key}");
                        cached_localization.Add(entry.Key, entry.Value);
                    }
                }
                if (extra_keys.Count > 0) {
                    Logger.LogDebug($"Removing extra keys {string.Join(",", extra_keys)}.");
                    foreach (string key in extra_keys) {
                        cached_localization.Remove(key);
                    }
                }
            }
            return cached_localization;
        }

        // This reads an embedded file resource name, these are all resources packed into the DLL
        // they all have a format following this:
        // ValheimArmory.localizations.English.json
        private static string ReadEmbeddedResourceFile(string filename) {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(filename);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
