using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace WhisperVoice
{
    // ── ModelEntry ─────────────────────────────────────────────────────────
    public class ModelEntry
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public override string ToString() => Name;
    }

    // ── SettingsWindow ──────────────────────────────────────────────────────
    public partial class SettingsWindow : Window
    {
        private AppSettings _settings;

        private string BaseDir => AppDomain.CurrentDomain.BaseDirectory;

        private static readonly string[] HotkeyOptions =
            { "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12" };

        // Display name ↔ whisper.cpp language code (transcription language)
        private static readonly Dictionary<string, string> LanguageMap = new()
        {
            { "English",    "en" },
            { "Русский",    "ru" },
            { "Українська", "uk" },
            { "Polski",     "pl" },
            { "Deutsch",    "de" },
            { "Español",    "es" },
            { "Français",   "fr" }
        };

        // Display name ↔ interface language code (same 7 languages, reused)
        private static readonly Dictionary<string, string> AppLangMap = new()
        {
            { "English",    "en" },
            { "Русский",    "ru" },
            { "Українська", "uk" },
            { "Polski",     "pl" },
            { "Deutsch",    "de" },
            { "Español",    "es" },
            { "Français",   "fr" }
        };

        // ── Constructor ────────────────────────────────────────────────────
        public SettingsWindow()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
            LoadModels();
            LoadSettings();
        }

        // ══════════════════════════════════════════════════════════════════
        // Model selector
        // ══════════════════════════════════════════════════════════════════
        private void LoadModels()
        {
            try
            {
                ModelCombo.Items.Clear();
                string modelsDir = Path.Combine(BaseDir, "models");

                // Безопасно проверяем существование, ничего не пытаемся создавать  
                if (Directory.Exists(modelsDir))
                {
                    string[] files = Directory.GetFiles(modelsDir, "*.bin");
                    foreach (var file in files)
                    {
                        ModelCombo.Items.Add(new ModelEntry { Name = Path.GetFileName(file), Path = file });
                    }
                }

                // Пытаемся выбрать сохраненную модель  
                if (!string.IsNullOrEmpty(_settings.LastModelPath))
                {
                    var match = ModelCombo.Items.Cast<ModelEntry>().FirstOrDefault(m => m.Path == _settings.LastModelPath);
                    if (match != null) ModelCombo.SelectedItem = match;
                }
            }
            catch (Exception)
            {
                // Глушим АБСОЛЮТНО все ошибки (включая UnauthorizedAccessException).  
                // Если папки нет или нет прав, список моделей просто останется пустым,   
                // и сработает логика MissingModelWindow.  
            }
        }

        private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ModelCombo.SelectedItem is ModelEntry entry)
                _settings.LastModelPath = entry.Path;
        }

        private void BtnRefreshModels_Click(object sender, RoutedEventArgs e) => LoadModels();

        // ══════════════════════════════════════════════════════════════════
        // Load settings → UI
        // ══════════════════════════════════════════════════════════════════
        private void LoadSettings()
        {
            // ── Language ──────────────────────────────────────────────────
            LanguageCombo.ItemsSource = LanguageMap.Keys;

            string displayName = "Русский";
            foreach (var kvp in LanguageMap)
            {
                if (kvp.Value == _settings.LanguagePrimary)
                {
                    displayName = kvp.Key;
                    break;
                }
            }
            LanguageCombo.SelectedItem = displayName;

            // ── VAD ───────────────────────────────────────────────────────
            SldVadThreshold.Value = Math.Clamp(_settings.VadThreshold,     1.0, 20.0);
            SldVadSilence.Value   = Math.Clamp(_settings.VadSilenceSeconds, 0.5,  5.0);

            // ── App interface language ────────────────────────────────────
            AppLanguageCombo.ItemsSource = AppLangMap.Keys;

            string currentAppLang = _settings.AppInterfaceLanguage;
            string appLangDisplay = "English";
            foreach (var kvp in AppLangMap)
            {
                if (kvp.Value == currentAppLang) { appLangDisplay = kvp.Key; break; }
            }
            AppLanguageCombo.SelectedItem = appLangDisplay;

            // ── Hotkeys ───────────────────────────────────────────────────
            ComboHotkeyPrimary.ItemsSource   = HotkeyOptions;
            ComboHotkeyTranslate.ItemsSource = HotkeyOptions;

            ComboHotkeyPrimary.SelectedItem = HotkeyOptions.Contains(_settings.HotkeyPrimary)
                ? _settings.HotkeyPrimary : "F8";

            ComboHotkeyTranslate.SelectedItem = HotkeyOptions.Contains(_settings.HotkeyTranslate)
                ? _settings.HotkeyTranslate : "F9";
        }

        // ══════════════════════════════════════════════════════════════════
        // App interface language — live runtime switch
        // ══════════════════════════════════════════════════════════════════
        private void AppLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AppLanguageCombo.SelectedItem is string displayName &&
                AppLangMap.TryGetValue(displayName, out string? langCode))
            {
                _settings.AppInterfaceLanguage = langCode;
                // Swap ResourceDictionary immediately — no restart required.
                App.ApplyInterfaceLanguage(langCode);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // VAD slider live-label updates
        // ══════════════════════════════════════════════════════════════════
        private void SldVadThreshold_ValueChanged(
            object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtVadThreshold != null)
                TxtVadThreshold.Text = $"{SldVadThreshold.Value:F1}";
        }

        private void SldVadSilence_ValueChanged(
            object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtVadSilence != null)
                TxtVadSilence.Text = $"{SldVadSilence.Value:F1} с";
        }

        // ══════════════════════════════════════════════════════════════════
        // Save & Close
        // ══════════════════════════════════════════════════════════════════
        private void BtnSaveClose_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            Close();
        }

        private void SaveSettings()
        {
            // Transcription language
            if (LanguageCombo.SelectedItem is string lang &&
                LanguageMap.TryGetValue(lang, out string? code))
                _settings.LanguagePrimary = code;

            // App interface language (already applied live; persist to disk)
            if (AppLanguageCombo.SelectedItem is string appLang &&
                AppLangMap.TryGetValue(appLang, out string? appCode))
                _settings.AppInterfaceLanguage = appCode;

            // Model (also updated live via ModelCombo_SelectionChanged)
            if (ModelCombo.SelectedItem is ModelEntry entry)
                _settings.LastModelPath = entry.Path;

            // VAD
            _settings.VadThreshold      = SldVadThreshold.Value;
            _settings.VadSilenceSeconds = SldVadSilence.Value;

            // Hotkeys — renamed Primary / Translate
            if (ComboHotkeyPrimary.SelectedItem is string hkPrimary)
                _settings.HotkeyPrimary = hkPrimary;

            if (ComboHotkeyTranslate.SelectedItem is string hkTranslate)
                _settings.HotkeyTranslate = hkTranslate;

            _settings.Save();
        }
    }
}
