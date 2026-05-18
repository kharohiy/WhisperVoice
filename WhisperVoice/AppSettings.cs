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
        /// <summary>Hotkey for Record with forced technical prompt injection.</summary>
        public string HotkeyPrompt { get; set; } = "F10";
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
                _ => "en"
            };
        }

        // ── Prompts ────────────────────────────────────────────────────
        /// <summary>Prompt used when HotkeyTranslate fires (translate mode). Empty = no prompt.</summary>
        public string PromptTranslate { get; set; } = "";

        // ── VAD ────────────────────────────────────────────────────────────
        /// <summary>Peak percentage below which the microphone is considered silent.</summary>
        public double VadThreshold { get; set; } = 5.0;
        /// <summary>Continuous silence duration (seconds) that auto-triggers stop.</summary>
        public double VadSilenceSeconds { get; set; } = 1.8;

        // ── Whisper inference params ───────────────────────────────────────
        /// <summary>--beam-size N  (1–10). Default 5 matches whisper.cpp default.</summary>
        public int BeamSize { get; set; } = 5;
        /// <summary>--best-of N  (1–10). Candidates sampled when beam_size == 1.</summary>
        public int BestOf { get; set; } = 5;
        /// <summary>--temperature F  (0.0–1.0). 0 = greedy / deterministic.</summary>
        public double Temperature { get; set; } = 0.0;
        /// <summary>--no-speech-thold F  (0.0–1.0). Segments below this probability are suppressed.</summary>
        public double NoSpeechThreshold { get; set; } = 0.6;

        // ── UI behaviour ───────────────────────────────────────────────────
        /// <summary>Play system sounds on recording start/stop when true.</summary>
        public bool SoundNotifications { get; set; } = true;

        /// <summary>
        /// When true (default), copies the final transcription text to the clipboard
        /// and auto-pastes via Ctrl+V after each recognition cycle.
        /// Disable to keep transcriptions in history only.
        /// </summary>
        public bool AutoClipboardCopy { get; set; } = true;

        // ── Hotkey mode ────────────────────────────────────────────────────
        /// <summary>
        /// When false (default): Toggle mode — one press starts, next press stops.
        ///   Uses NHotkey (RegisterHotKey Win32 API).
        /// When true: Push-to-Talk mode — hold to record, release to stop.
        ///   Uses a low-level WH_KEYBOARD_LL keyboard hook.
        /// Only applies to the Primary and Translate hotkeys.
        /// The menu, notepad, and Ctrl+F9 hotkeys are always in Toggle mode.
        /// </summary>
        public bool IsPushToTalkEnabled { get; set; } = false;

        // ── Static helpers ─────────────────────────────────────────────────

        private static readonly JsonSerializerOptions _jsonOpts =
            new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

        /// <summary>App data directory: exe folder in DEBUG, AppData in RELEASE.</summary>
        public static string AppDataDir
        {
            get
            {
#if DEBUG
                return AppDomain.CurrentDomain.BaseDirectory;
#else
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WhisperVoice");
#endif
            }
        }

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
            if (File.Exists(SettingsPath))
            {
                try
                {
                    string json = File.ReadAllText(SettingsPath);

                    json = json
                        .Replace("\"HotkeyRu\"", "\"HotkeyPrimary\"")
                        .Replace("\"HotkeyEn\"", "\"HotkeyTranslate\"")
                        .Replace("\"LanguageF8\"", "\"LanguagePrimary\"");

                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, _jsonOpts)
                                 ?? new AppSettings();

                    loaded.Save();
                    return loaded;
                }
                catch (Exception ex) { DiagnosticLogger.Instance.Error("AppSettings", ex, "I/O operation failed"); }
            }

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
                catch (Exception ex) { DiagnosticLogger.Instance.Error("AppSettings", ex, "I/O operation failed"); }
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
            catch (Exception ex) { DiagnosticLogger.Instance.Error("AppSettings", ex, "I/O operation failed"); }
        }

        /// <summary>Convenience: returns true if a microphone has been configured.</summary>
        [JsonIgnore]
        public bool HasMic => !string.IsNullOrWhiteSpace(MicId);
    }
}
