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

        // ── Language for F8 hotkey (transcription) ──────────────────────────
        /// <summary>Language code for F8 direct transcription (default: "en").</summary>
        public string LanguageF8 { get; set; } = "en";

        // ── Hotkeys (display names only – actual binding is in MainWindow) ──
        public string HotkeyRu { get; set; } = "F8";
        public string HotkeyEn { get; set; } = "F9";
        public string HotkeyTranslate { get; set; } = "Ctrl+F9";
        public string HotkeyMenu { get; set; } = "F7";
        public string HotkeyNotepad { get; set; } = "Ctrl+F7";

        // ── VAD ────────────────────────────────────────────────────────────
        /// <summary>Peak percentage below which the microphone is considered silent.</summary>
        public double VadThreshold { get; set; } = 5.0;
        /// <summary>Continuous silence duration (seconds) that auto-triggers stop.</summary>
        public double VadSilenceSeconds { get; set; } = 1.8;

        // ── Static helpers ─────────────────────────────────────────────────

        private static readonly JsonSerializerOptions _jsonOpts =
            new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

        private static string SettingsPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        private static string LegacyIniPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");

        /// <summary>
        /// Load settings.json, or migrate from settings.ini if it exists,
        /// or return a default instance if neither file is present.
        /// </summary>
        public static AppSettings Load()
        {
            // 1 – Try settings.json
            if (File.Exists(SettingsPath))
            {
                try
                {
                    string json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json, _jsonOpts)
                           ?? new AppSettings();
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
                    migrated.Save();   // persist as JSON immediately
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
