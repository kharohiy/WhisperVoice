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

        // Display name ↔ whisper.cpp language code
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
        internal void LoadModels()
        {
            string modelsDir = System.IO.Path.Combine(BaseDir, "models");
            ModelCombo.Items.Clear();

            if (Directory.Exists(modelsDir))
            {
                foreach (string file in Directory.GetFiles(modelsDir, "*.bin").OrderBy(f => f))
                {
                    ModelCombo.Items.Add(new ModelEntry
                    {
                        Name = System.IO.Path.GetFileNameWithoutExtension(file),
                        Path = file
                    });
                }
            }

            if (ModelCombo.Items.Count == 0)
                ModelCombo.Items.Add(new ModelEntry
                {
                    Name = "ggml-large-v3 (по умолчанию)",
                    Path = System.IO.Path.Combine(BaseDir, "models", "ggml-large-v3.bin")
                });

            if (!string.IsNullOrEmpty(_settings.LastModelPath))
            {
                var saved = ModelCombo.Items.Cast<ModelEntry>()
                    .FirstOrDefault(m => m.Path == _settings.LastModelPath);
                if (saved != null) { ModelCombo.SelectedItem = saved; return; }
            }

            ModelCombo.SelectedIndex = 0;
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

            // ── Hotkeys ───────────────────────────────────────────────────
            ComboHotkeyPrimary.ItemsSource   = HotkeyOptions;
            ComboHotkeyTranslate.ItemsSource = HotkeyOptions;

            ComboHotkeyPrimary.SelectedItem = HotkeyOptions.Contains(_settings.HotkeyPrimary)
                ? _settings.HotkeyPrimary : "F8";

            ComboHotkeyTranslate.SelectedItem = HotkeyOptions.Contains(_settings.HotkeyTranslate)
                ? _settings.HotkeyTranslate : "F9";
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
            // Language
            if (LanguageCombo.SelectedItem is string lang &&
                LanguageMap.TryGetValue(lang, out string? code))
                _settings.LanguagePrimary = code;

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
