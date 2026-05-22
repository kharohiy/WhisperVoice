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

        // ── Profiles ───────────────────────────────────────────────────────
        public System.Collections.Generic.List<WhisperProfile> CustomProfiles { get; set; } = new();
        public string PrimaryProfileId { get; set; } = "dev";
        public string TranslateProfileId { get; set; } = "none";
        public string PromptProfileId { get; set; } = "dev";

        public void InitializeDefaultProfiles()
        {
            if (!CustomProfiles.Any(p => p.Id == "none"))
            {
                CustomProfiles.Insert(0, new WhisperProfile
                {
                    Id = "none", Name = "None (Standard Whisper)", Temperature = 0.2, IsPredefined = true,
                    PromptTags = ""
                });
            }
            else
            {
                var existingNone = CustomProfiles.First(p => p.Id == "none");
                if (existingNone.Name == "Без профиля (Стандарт)")
                {
                    existingNone.Name = "None (Standard Whisper)";
                }
            }

            if (!CustomProfiles.Any(p => p.Id == "dev"))
            {
                CustomProfiles.Insert(Math.Min(0, CustomProfiles.Count), new WhisperProfile
                {
                    Id = "dev", Name = "Developer", Temperature = 0.2, IsPredefined = true,
                    PromptTags = "C#, Python, IDE, Visual Studio, Git, Code, Programming, Syntax, Bug, Async, Await, Function, Class, Variable, Array, Dictionary, JSON, API, Debugging"
                });
            }

            if (!CustomProfiles.Any(p => p.Id == "eng"))
            {
                CustomProfiles.Insert(Math.Min(1, CustomProfiles.Count), new WhisperProfile
                {
                    Id = "eng", Name = "English Teacher", Temperature = 0.1, IsPredefined = true,
                    PromptTags = "Grammar, Punctuation, Spelling, Academic English, Formal, Vocabulary, Pronunciation, Sentence structure, Syntax, Adjective, Noun, Verb, Pronoun, Adverb"
                });
            }

            if (!CustomProfiles.Any(p => p.Id == "copy"))
            {
                CustomProfiles.Insert(Math.Min(2, CustomProfiles.Count), new WhisperProfile
                {
                    Id = "copy", Name = "Copywriter", Temperature = 0.4, IsPredefined = true,
                    PromptTags = "Paragraphs, commas, exclamation marks, clear structure, storytelling, marketing, engagement, SEO, blog, article, sales, audience, headline, hook"
                });
            }

            if (!CustomProfiles.Any(p => p.Id == "biz"))
            {
                CustomProfiles.Insert(Math.Min(3, CustomProfiles.Count), new WhisperProfile
                {
                    Id = "biz", Name = "Business / Corporate", Temperature = 0.2, IsPredefined = true,
                    PromptTags = "Meeting, agenda, action items, KPI, management, strategy, marketing, finance, ROI, CEO, deadlines, B2B, B2C, stakeholder, revenue, budget, quarterly"
                });
            }

            if (!CustomProfiles.Any(p => p.Id == "med"))
            {
                CustomProfiles.Insert(Math.Min(4, CustomProfiles.Count), new WhisperProfile
                {
                    Id = "med", Name = "Medical / Science", Temperature = 0.1, IsPredefined = true,
                    PromptTags = "Diagnosis, treatment, anatomy, clinical, research, laboratory, physics, biology, symptoms, doctor, patient, disease, syndrome, therapy, medication, surgery"
                });
            }

            // Removed legacy Default mappings overwriting here. 
            // They are now handled by property defaults so explicit nulls are preserved.
        }

        // ── VAD ────────────────────────────────────────────────────────────
        /// <summary>Peak percentage below which the microphone is considered silent.</summary>
        public double VadThreshold { get; set; } = 5.0;
        /// <summary>Continuous silence duration (seconds) that auto-triggers stop.</summary>
        public double VadSilenceSeconds { get; set; } = 1.8;

        // ── Whisper inference params ───────────────────────────────────────
        /// <summary>--beam-size N  (1–10). Default 5 matches whisper.cpp default.</summary>
        public int BeamSize { get; set; } = 1; // Changed to 1 to prevent VRAM KV Cache explosion
        /// <summary>--best-of N  (1–10). Candidates sampled when beam_size == 1.</summary>
        public int BestOf { get; set; } = 1;
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

        /// <summary>
        /// Gets or sets a value indicating whether it's the first run of the application.
        /// </summary>
        public bool IsFirstRun { get; set; } = true;

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

        // ── Whitelist ──────────────────────────────────────────────────────
        public string[] WhitelistedDomains { get; set; } = new[] { "raw.githubusercontent.com", "huggingface.co" };

        // ── Settings versioning ──────────────────────────────────────
        /// <summary>
        /// Version of the settings schema.
        /// Used for detecting when migration is needed on load.
        /// Version 1: legacy HotkeyRu / HotkeyEn keys.
        /// Version 2: current schema with HotkeyPrimary / HotkeyTranslate.
        /// </summary>
        public int SettingsVersion { get; set; } = 2;

        // ── Static helpers ─────────────────────────────────────────────────

        private static readonly JsonSerializerOptions _jsonOpts =
            new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

        /// <summary>
        /// Migrates legacy JSON key names to current schema.
        /// Idempotent — safe to call on already-current JSON.
        /// </summary>
        public static string MigrateJsonIfNeeded(string json)
        {
            // V1 → V2: rename hotkey and language keys
            if (json.Contains("\"HotkeyRu\"") || json.Contains("\"HotkeyEn\"") || json.Contains("\"LanguageF8\""))
            {
                DiagnosticLogger.Instance.Info("AppSettings", "Migrating legacy settings keys (V1 → V2)");
                json = json
                    .Replace("\"HotkeyRu\"",  "\"HotkeyPrimary\"")
                    .Replace("\"HotkeyEn\"",  "\"HotkeyTranslate\"")
                    .Replace("\"LanguageF8\"", "\"LanguagePrimary\"");
            }

            // V3 migrations would go here...

            return json;
        }


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

        /// <summary>Models directory: always execution base / models folder.</summary>
        public static string ModelsDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");

        /// <summary>Resolved models directory for monolithic compatibility.</summary>
        [JsonIgnore]
        public string ResolvedModelsDir => ModelsDir;

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
                    string raw  = File.ReadAllText(SettingsPath);
                    string json = MigrateJsonIfNeeded(raw);

                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, _jsonOpts)
                                 ?? new AppSettings();

                    loaded.InitializeDefaultProfiles();
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
                    migrated.InitializeDefaultProfiles();
                    migrated.Save();
                    return migrated;
                }
                catch (Exception ex) { DiagnosticLogger.Instance.Error("AppSettings", ex, "I/O operation failed"); }
            }

            var def = new AppSettings();
            def.InitializeDefaultProfiles();
            return def;
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
