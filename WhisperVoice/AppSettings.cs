using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisperVoice
{
    /// <summary>
    /// Persistent application settings serialised as settings.json.
    /// Automatically migrates from the legacy settings.ini on first load.
    /// </summary>
    public class AppSettings
    {
        // ── Microphone ─────────────────────────────────────────────────────
        public string MicId { get; set; } = "";
        public string MicName { get; set; } = "";

        // ── Model ──────────────────────────────────────────────────────────
        /// <summary>Full path of the last selected .bin model file.</summary>
        public string LastModelPath { get; set; } = "";

        // ── Language for Primary hotkey (transcription) ────────────────────
        /// <summary>Language code for primary-hotkey direct transcription (default: "en").</summary>
        public string LanguagePrimary { get; set; } = "en";

        // ── Hotkeys (display names only – actual binding is in MainWindow) ──
        /// <summary>Hotkey for Record in selected language (was HotkeyRu).</summary>
        public string HotkeyPrimary { get; set; } = "F8";
        /// <summary>Hotkey for Record with forced English translation (was HotkeyEn).</summary>
        public string HotkeyTranslate { get; set; } = "F9";
        public string HotkeyMenu { get; set; } = "F7";
        public string HotkeyNotepad { get; set; } = "Ctrl+F7";

        // ── App interface language ─────────────────────────────────────────
        /// <summary>
        /// BCP-47-like language code that controls which Strings.xaml variant is loaded.
        /// Supported values: "en", "ru", "uk", "pl", "de", "es", "fr".
        /// Defaults to the OS UI culture, falling back to "en".
        /// </summary>
        public string AppInterfaceLanguage { get; set; } =
            DefaultInterfaceLanguage();

        private static string DefaultInterfaceLanguage()
        {
            string culture = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return culture switch
            {
                "ru" => "ru",
                "uk" => "uk",
                "pl" => "pl",
                "de" => "de",
                "es" => "es",
                "fr" => "fr",
                _    => "en"
            };
        }

        // ── VAD ────────────────────────────────────────────────────────────
        /// <summary>Peak percentage below which the microphone is considered silent.</summary>
        public double VadThreshold { get; set; } = 5.0;
        /// <summary>Continuous silence duration (seconds) that auto-triggers stop.</summary>
        public double VadSilenceSeconds { get; set; } = 1.8;

        // ── Static helpers ─────────────────────────────────────────────────

        private static readonly JsonSerializerOptions _jsonOpts =
            new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

        /// <summary>%LocalAppData%\WhisperVoice — the single writable root for all user data.</summary>
        public static string AppDataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "WhisperVoice");

        private static string SettingsPath =>
            Path.Combine(AppDataDir, "settings.json");

        private static string LegacyIniPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");

        /// <summary>
        /// Load settings.json, or migrate from settings.ini if it exists,
        /// or return a default instance if neither file is present.
        /// Also handles migration of legacy HotkeyRu/HotkeyEn property names.
        /// </summary>
        public static AppSettings Load()
        {
            // 1 – Try settings.json
            if (File.Exists(SettingsPath))
            {
                try
                {
                    string json = File.ReadAllText(SettingsPath);

                    // ── Legacy property migration ──────────────────────────
                    // Old JSON may still contain "HotkeyRu" / "HotkeyEn".
                    // Rename them before deserialising so no data is lost.
                    json = json
                        .Replace("\"HotkeyRu\"", "\"HotkeyPrimary\"")
                        .Replace("\"HotkeyEn\"", "\"HotkeyTranslate\"")
                        .Replace("\"LanguageF8\"", "\"LanguagePrimary\"");

                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, _jsonOpts)
                                 ?? new AppSettings();

                    // Persist migrated names immediately so the old keys disappear
                    loaded.Save();
                    return loaded;
                }
                catch { /* corrupt JSON — fall through to defaults */ }
            }

            // 2 – Migrate legacy settings.ini
            if (File.Exists(LegacyIniPath))
            {
                try
                {
                    string[] lines = File.ReadAllLines(LegacyIniPath);
                    var migrated = new AppSettings
                    {
                        MicId = lines.Length > 0 ? lines[0].Trim() : "",
                        MicName = lines.Length > 1 ? lines[1].Trim() : ""
                    };
                    migrated.Save();
                    return migrated;
                }
                catch { /* bad INI — return defaults */ }
            }

            return new AppSettings();
        }

        /// <summary>Persist the current state to settings.json.</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                File.WriteAllText(SettingsPath,
                    JsonSerializer.Serialize(this, _jsonOpts));
            }
            catch { /* non-critical */ }
        }

        /// <summary>Convenience: returns true if a microphone has been configured.</summary>
        [JsonIgnore]
        public bool HasMic => !string.IsNullOrWhiteSpace(MicId);
    }
}