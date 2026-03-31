using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace WhisperVoice
{
    // ── ModelEntry moved here from MainWindow ───────────────────────────────
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

        // Valid hotkey options shown in the ComboBoxes
        private static readonly string[] HotkeyOptions =
            { "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12" };

        // Display name ↔ Whisper language code
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
        // Model selector  (moved from MainWindow)
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

            // Always guarantee at least one entry
            if (ModelCombo.Items.Count == 0)
                ModelCombo.Items.Add(new ModelEntry
                {
                    Name = "ggml-large-v3 (по умолчанию)",
                    Path = System.IO.Path.Combine(BaseDir, "models", "ggml-large-v3.bin")
                });

            // Restore last-used model
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
            // Track selection in _settings but do not write to disk until Save is clicked
            if (ModelCombo.SelectedItem is ModelEntry entry)
                _settings.LastModelPath = entry.Path;
        }

        private void BtnRefreshModels_Click(object sender, RoutedEventArgs e) => LoadModels();

        // ══════════════════════════════════════════════════════════════════
        // Load all settings into UI controls
        // ══════════════════════════════════════════════════════════════════
        private void LoadSettings()
        {
            // ── Language ──────────────────────────────────────────────────
            LanguageCombo.ItemsSource = LanguageMap.Keys;

            string displayName = "Русский";   // fallback
            foreach (var kvp in LanguageMap)
            {
                if (kvp.Value == _settings.LanguageF8)
                {
                    displayName = kvp.Key;
                    break;
                }
            }
            LanguageCombo.SelectedItem = displayName;

            // ── VAD ───────────────────────────────────────────────────────
            SldVadThreshold.Value = Math.Clamp(_settings.VadThreshold, 1.0, 20.0);
            SldVadSilence.Value   = Math.Clamp(_settings.VadSilenceSeconds, 0.5, 5.0);
            // Labels are refreshed via the ValueChanged handlers triggered above

            // ── Hotkeys ───────────────────────────────────────────────────
            ComboHotkeyRu.ItemsSource = HotkeyOptions;
            ComboHotkeyEn.ItemsSource = HotkeyOptions;

            ComboHotkeyRu.SelectedItem = HotkeyOptions.Contains(_settings.HotkeyRu)
                ? _settings.HotkeyRu : "F8";

            ComboHotkeyEn.SelectedItem = HotkeyOptions.Contains(_settings.HotkeyEn)
                ? _settings.HotkeyEn : "F9";
        }

        // ══════════════════════════════════════════════════════════════════
        // VAD slider live-label updates
        // ══════════════════════════════════════════════════════════════════
        private void SldVadThreshold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtVadThreshold != null)
                TxtVadThreshold.Text = $"{SldVadThreshold.Value:F1}";
        }

        private void SldVadSilence_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
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
                LanguageMap.TryGetValue(lang, out string code))
                _settings.LanguageF8 = code;

            // Model  (also updated live via ModelCombo_SelectionChanged)
            if (ModelCombo.SelectedItem is ModelEntry entry)
                _settings.LastModelPath = entry.Path;

            // VAD
            _settings.VadThreshold      = SldVadThreshold.Value;
            _settings.VadSilenceSeconds = SldVadSilence.Value;

            // Hotkeys
            if (ComboHotkeyRu.SelectedItem is string hkRu)
                _settings.HotkeyRu = hkRu;
            if (ComboHotkeyEn.SelectedItem is string hkEn)
                _settings.HotkeyEn = hkEn;

            _settings.Save();
        }
    }
}
