using System;
using System.Windows;

namespace WhisperVoice
{
    public partial class App : System.Windows.Application
    {
        // Supported interface language codes (must match Strings.{lang}.xaml filenames)
        private static readonly string[] SupportedLangs =
            { "en", "ru", "uk", "pl", "de", "es", "fr" };

        /// <summary>
        /// Swaps the merged ResourceDictionary for localised strings at runtime.
        /// All DynamicResource bindings update automatically across every open window.
        /// Call this from SettingsWindow after saving AppInterfaceLanguage.
        /// </summary>
        public static void ApplyInterfaceLanguage(string langCode)
        {
            // Clamp to a supported code
            bool known = Array.Exists(SupportedLangs, l => l == langCode);
            if (!known) langCode = "en";

            string uri = $"Resources/Strings.{langCode}.xaml";
            var dict = new ResourceDictionary
            {
                Source = new Uri(uri, UriKind.Relative)
            };

            // Replace the first merged dictionary (the strings file) in-place.
            // Using Replace instead of Clear+Add keeps other merged dicts intact.
            var merged = Current.Resources.MergedDictionaries;
            if (merged.Count > 0)
                merged[0] = dict;
            else
                merged.Add(dict);
        }
    }
}
