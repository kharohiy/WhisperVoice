using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

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
        // Windows startup
        // ══════════════════════════════════════════════════════════════════
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName    = "WhisperVoice";

        private void LoadStartupCheckbox()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);

            string? savedValue = key?.GetValue(AppName) as string;
            string currentExe = $"\"{Environment.ProcessPath}\" --autostart";

            // Only check if the registry value matches THIS exe
            bool isRegistered = savedValue != null &&
                savedValue.Equals(currentExe, StringComparison.OrdinalIgnoreCase);

            ChkRunAtStartup.Checked -= ChkRunAtStartup_Changed;
            ChkRunAtStartup.Unchecked -= ChkRunAtStartup_Changed;
            ChkRunAtStartup.IsChecked = isRegistered;
            ChkRunAtStartup.Checked += ChkRunAtStartup_Changed;
            ChkRunAtStartup.Unchecked += ChkRunAtStartup_Changed;
        }

        private void ChkRunAtStartup_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key == null) return;

                if (ChkRunAtStartup.IsChecked == true)
                {
                    // Always use the actual running exe path — never a hardcoded string
                    string exePath = Environment.ProcessPath ??
                                     System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
                    key.SetValue(AppName, $"\"{exePath}\" --autostart");
                }
                else
                {
                    key.DeleteValue(AppName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex) { DiagnosticLogger.Instance.Error("SettingsWindow", ex, "Registry operation failed"); }
        }


        // ══════════════════════════════════════════════════════════════════
        // Model selector
        // ══════════════════════════════════════════════════════════════════
        public void LoadModels()
        {
            ModelCombo.Items.Clear();
            string modelsDir = AppSettings.ModelsDir;

            try
            {
                if (!Directory.Exists(modelsDir))
                {
                    // Show placeholder when folder doesn't exist
                    ModelCombo.Items.Add(new ModelEntry
                    {
                        Name = "No models found — click Add to import",
                        Path = ""
                    });
                    return;
                }

                string[] files = Directory.GetFiles(modelsDir, "*.bin");

                if (files.Length == 0)
                {
                    ModelCombo.Items.Add(new ModelEntry
                    {
                        Name = "No models found — click Add to import",
                        Path = ""
                    });
                    return;
                }

                foreach (var file in files)
                {
                    ModelCombo.Items.Add(new ModelEntry
                    {
                        Name = Path.GetFileName(file),
                        Path = file
                    });
                }

                // Auto-select saved model
                if (!string.IsNullOrEmpty(_settings.LastModelPath))
                {
                    var match = ModelCombo.Items.Cast<ModelEntry>()
                        .FirstOrDefault(m => m.Path == _settings.LastModelPath);
                    if (match != null)
                        ModelCombo.SelectedItem = match;
                }
            }
            catch (UnauthorizedAccessException)
            {
                ModelCombo.Items.Add(new ModelEntry
                {
                    Name = TryGetResource("MsgModelLoadErrorAccess", "Access denied — check permissions"),
                    Path = ""
                });
            }
            catch (Exception ex)
            {
                ModelCombo.Items.Add(new ModelEntry
                {
                    Name = string.Format(TryGetResource("MsgModelLoadError", "Error: {0}"), ex.Message),
                    Path = ""
                });
            }
        }

        private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ModelCombo.SelectedItem is ModelEntry entry && !string.IsNullOrEmpty(entry.Path))
                _settings.LastModelPath = entry.Path;
        }

        private void BtnRefreshModels_Click(object sender, RoutedEventArgs e) => LoadModels();

        private void BtnGetModels_Click(object sender, RoutedEventArgs e)
        {
            string modelsDir = AppSettings.ModelsDir;
            var win = new WhisperVoice.Views.ModelsWindow(modelsDir, onModelAdded: LoadModels)
            {
                Owner = this
            };
            win.Show();
        }

        private void BtnAddModel_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Whisper Model",
                Filter = "Whisper Models (*.bin)|*.bin|All Files (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            if (dialog.ShowDialog() != true)
                return;

            string sourceFile = dialog.FileName;
            string modelsDir = AppSettings.ModelsDir;

            try
            {
                // Create models folder if missing
                if (!Directory.Exists(modelsDir))
                    Directory.CreateDirectory(modelsDir);

                string fileName = Path.GetFileName(sourceFile);
                string destPath = Path.Combine(modelsDir, fileName);

                // Check if already exists
                if (File.Exists(destPath))
                {
                    var result = System.Windows.MessageBox.Show(
                        string.Format(TryGetResource("MsgModelExistsBody", "Model '{0}' already exists.\nOverwrite?"), fileName),
                        TryGetResource("MsgModelExistsTitle", "Model exists"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result != MessageBoxResult.Yes)
                        return;
                }

                // Copy model file
                File.Copy(sourceFile, destPath, overwrite: true);

                // Refresh list and auto-select new model
                LoadModels();
                var newEntry = ModelCombo.Items.Cast<ModelEntry>()
                    .FirstOrDefault(m => m.Path == destPath);
                if (newEntry != null)
                    ModelCombo.SelectedItem = newEntry;

                System.Windows.MessageBox.Show(
                    string.Format(TryGetResource("MsgModelAddedBody", "Model '{0}' added successfully."), fileName),
                    TryGetResource("MsgModelAddedTitle", "Success"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (UnauthorizedAccessException)
            {
                System.Windows.MessageBox.Show(
                    TryGetResource("MsgModelAccessDeniedBody", "Access denied. Run as administrator or choose a different destination."),
                    TryGetResource("MsgModelAccessDeniedTitle", "Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format(TryGetResource("MsgModelFailedBody", "Failed to add model:\n{0}"), ex.Message),
                    TryGetResource("MsgModelFailedTitle", "Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Load settings → UI
        // ══════════════════════════════════════════════════════════════════
        private void LoadSettings()
        {
            // ── Language ──────────────────────────────────────────────────
            LanguageCombo.ItemsSource = LanguageMap.Keys;

            string displayName = "English";
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
            SldVadThreshold.Value = Math.Clamp(_settings.VadThreshold, 1.0, 20.0);
            SldVadSilence.Value = Math.Clamp(_settings.VadSilenceSeconds, 0.5, 5.0);

            // ── Whisper inference params ──────────────────────────────────
            SldBeamSize.Value      = Math.Clamp(_settings.BeamSize, 1, 10);
            SldBestOf.Value        = Math.Clamp(_settings.BestOf, 1, 10);
            SldTemperature.Value   = Math.Clamp(_settings.Temperature, 0.0, 1.0);
            SldNoSpeechThold.Value = Math.Clamp(_settings.NoSpeechThreshold, 0.0, 1.0);

            // ── Sound notifications ───────────────────────────────────────
            ChkSoundNotifications.IsChecked = _settings.SoundNotifications;

            // ── Auto clipboard copy ───────────────────────────────────────
            ChkAutoClipboard.IsChecked = _settings.AutoClipboardCopy;

            // ── Push-to-Talk mode ─────────────────────────────────────────
            ChkPushToTalk.IsChecked = _settings.IsPushToTalkEnabled;

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
            ComboHotkeyPrimary.ItemsSource = HotkeyOptions;
            ComboHotkeyTranslate.ItemsSource = HotkeyOptions;
            ComboHotkeyPrompt.ItemsSource = HotkeyOptions;

            ComboHotkeyPrimary.SelectedItem = HotkeyOptions.Contains(_settings.HotkeyPrimary)
                ? _settings.HotkeyPrimary : "F8";

            ComboHotkeyTranslate.SelectedItem = HotkeyOptions.Contains(_settings.HotkeyTranslate)
                ? _settings.HotkeyTranslate : "F9";

            ComboHotkeyPrompt.SelectedItem = HotkeyOptions.Contains(_settings.HotkeyPrompt)
                ? _settings.HotkeyPrompt : "F10";

            // ── Startup ───────────────────────────────────────────────────
            LoadStartupCheckbox();
        }

        // ══════════════════════════════════════════════════════════════════
        // Tab Navigation
        // ══════════════════════════════════════════════════════════════════
        private void TabMenu_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PanelGeneral == null || PanelAudio == null || PanelHotkeys == null) return;

            PanelGeneral.Visibility = Visibility.Collapsed;
            PanelAudio.Visibility = Visibility.Collapsed;
            PanelHotkeys.Visibility = Visibility.Collapsed;

            switch (TabMenu.SelectedIndex)
            {
                case 0: PanelGeneral.Visibility = Visibility.Visible; break;
                case 1: PanelAudio.Visibility = Visibility.Visible; break;
                case 2: PanelHotkeys.Visibility = Visibility.Visible; break;
            }
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
                TxtVadSilence.Text = $"{SldVadSilence.Value:F1}{TryGetResource("UnitSeconds", " s")}";
        }

        private void SldBeamSize_ValueChanged(
            object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtBeamSize != null)
                TxtBeamSize.Text = ((int)SldBeamSize.Value).ToString();
        }

        private void SldBestOf_ValueChanged(
            object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtBestOf != null)
                TxtBestOf.Text = ((int)SldBestOf.Value).ToString();
        }

        private void SldTemperature_ValueChanged(
            object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtTemperature != null)
                TxtTemperature.Text = $"{SldTemperature.Value:F2}";
        }

        private void SldNoSpeechThold_ValueChanged(
            object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtNoSpeechThold != null)
                TxtNoSpeechThold.Text = $"{SldNoSpeechThold.Value:F2}";
        }

        private void BtnResetSliders_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show(
                TryGetResource("MsgResetSlidersBody", "Reset VAD settings and Whisper parameters to defaults?"),
                TryGetResource("MsgResetSlidersTitle", "Reset settings"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.OK) return;

            // VAD
            SldVadThreshold.Value = 5.0;
            SldVadSilence.Value   = 1.8;

            // Whisper inference params
            SldBeamSize.Value      = 5;
            SldBestOf.Value        = 5;
            SldTemperature.Value   = 0.0;
            SldNoSpeechThold.Value = 0.6;
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
            _settings.VadThreshold = SldVadThreshold.Value;
            _settings.VadSilenceSeconds = SldVadSilence.Value;

            // Whisper inference params
            _settings.BeamSize           = (int)SldBeamSize.Value;
            _settings.BestOf             = (int)SldBestOf.Value;
            _settings.Temperature        = SldTemperature.Value;
            _settings.NoSpeechThreshold  = SldNoSpeechThold.Value;

            // Sound notifications
            _settings.SoundNotifications = ChkSoundNotifications.IsChecked == true;

            // Auto clipboard copy
            _settings.AutoClipboardCopy = ChkAutoClipboard.IsChecked == true;

            // Push-to-Talk mode
            _settings.IsPushToTalkEnabled = ChkPushToTalk.IsChecked == true;

            // Hotkeys — renamed Primary / Translate
            if (ComboHotkeyPrimary.SelectedItem is string hkPrimary)
                _settings.HotkeyPrimary = hkPrimary;

            if (ComboHotkeyTranslate.SelectedItem is string hkTranslate)
                _settings.HotkeyTranslate = hkTranslate;

            if (ComboHotkeyPrompt.SelectedItem is string hkPrompt)
                _settings.HotkeyPrompt = hkPrompt;

            _settings.Save();
        }

        // ══════════════════════════════════════════════════════════════════
        // Localisation helper
        // ══════════════════════════════════════════════════════════════════
        private string TryGetResource(string key, string fallback)
        {
            try { return (string)FindResource(key); }
            catch { return fallback; }
        }
    }
}
