using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using WhisperVoice.Services;

namespace WhisperVoice
{
    public partial class FirstRunWizardWindow : Window
    {
        private int _currentStep = 1;
        private AppSettings _settings;
        private AudioRecorder? _testRecorder;
        private DateTime _lastMicPeakTime = DateTime.MinValue;

        private static readonly Dictionary<string, string> AppLangMap = new()
        {
            { "English", "en" },
            { "Русский", "ru" },
            { "Українська", "uk" },
            { "Deutsch", "de" },
            { "Español", "es" },
            { "Français", "fr" },
            { "Polski", "pl" }
        };

        private static readonly Dictionary<string, string> ModelLangMap = new()
        {
            { "Auto (Multi-language)", "" },
            { "English", "en" },
            { "Русский", "ru" },
            { "Українська", "uk" },
            { "Deutsch", "de" },
            { "Español", "es" },
            { "Français", "fr" },
            { "Polski", "pl" }
        };

        public FirstRunWizardWindow()
        {
            InitializeComponent();
            _settings = AppSettings.Load();

            CmbAppLanguage.ItemsSource = AppLangMap.Keys;
            CmbDictationLanguage.ItemsSource = ModelLangMap.Keys;

            LoadSettings();
            LoadMicrophones();
            LoadMicVolume();
            UpdateStepVisibility();
        }

        private void LoadSettings()
        {
            // App Language
            string appLangCode = _settings.AppInterfaceLanguage ?? "en";
            var appLangPair = AppLangMap.FirstOrDefault(x => x.Value == appLangCode);
            if (appLangPair.Key != null)
                CmbAppLanguage.SelectedItem = appLangPair.Key;
            else
                CmbAppLanguage.SelectedItem = "English";

            // Dictation Language
            string modelLangCode = _settings.LanguagePrimary ?? "";
            var modelLangPair = ModelLangMap.FirstOrDefault(x => x.Value == modelLangCode);
            if (modelLangPair.Key != null)
                CmbDictationLanguage.SelectedItem = modelLangPair.Key;
            else
                CmbDictationLanguage.SelectedItem = "Auto (Multi-language)";
        }

        private void LoadMicrophones()
        {
            CmbMicrophones.Items.Clear();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                
                var defaultItem = new ComboBoxItem { Content = "Default (System)", Tag = "" };
                CmbMicrophones.Items.Add(defaultItem);
                
                ComboBoxItem? selectedItem = defaultItem;

                foreach (var device in devices)
                {
                    var item = new ComboBoxItem { Content = device.FriendlyName, Tag = device.ID };
                    CmbMicrophones.Items.Add(item);
                    
                    if (device.ID == _settings.MicId)
                        selectedItem = item;
                }
                CmbMicrophones.SelectedItem = selectedItem;
            }
            catch { }
        }

        private float GetMicVolume()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice? device = null;
                if (!string.IsNullOrWhiteSpace(_settings.MicId))
                {
                    try { device = enumerator.GetDevice(_settings.MicId); } catch { }
                }
                if (device == null)
                    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                
                return device?.AudioEndpointVolume?.MasterVolumeLevelScalar ?? 1.0f;
            }
            catch { return 1.0f; }
        }

        private void SetMicVolume(float volume)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice? device = null;
                if (!string.IsNullOrWhiteSpace(_settings.MicId))
                {
                    try { device = enumerator.GetDevice(_settings.MicId); } catch { }
                }
                if (device == null)
                    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                
                if (device?.AudioEndpointVolume != null)
                {
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = volume;
                }
            }
            catch { }
        }

        private void LoadMicVolume()
        {
            SldMicVolume.Value = GetMicVolume();
        }

        private void SldMicVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (IsLoaded)
                SetMicVolume((float)e.NewValue);
        }

        private void CmbAppLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbAppLanguage.SelectedItem is string displayName && IsLoaded)
            {
                if (AppLangMap.TryGetValue(displayName, out string? langCode))
                {
                    _settings.AppInterfaceLanguage = langCode;
                    App.ApplyInterfaceLanguage(langCode);
                }
            }
        }

        private void UpdateStepVisibility()
        {
            PanelStep1.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelStep2.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
            PanelStep3.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;

            BtnBack.Visibility = _currentStep > 1 ? Visibility.Visible : Visibility.Collapsed;
            
            if (_currentStep == 3)
            {
                BtnNext.Visibility = Visibility.Collapsed;
                BtnFinish.Visibility = Visibility.Visible;
                RunModelAdvisor();
            }
            else
            {
                BtnNext.Visibility = Visibility.Visible;
                BtnFinish.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep == 1)
            {
                if (CmbDictationLanguage.SelectedItem is string displayName && ModelLangMap.TryGetValue(displayName, out string? langCode))
                    _settings.LanguagePrimary = langCode;
            }
            else if (_currentStep == 2)
            {
                if (CmbMicrophones.SelectedItem is ComboBoxItem item)
                    _settings.MicId = (string)item.Tag;
                
                StopMicTest();
            }

            if (_currentStep < 3)
            {
                _currentStep++;
                UpdateStepVisibility();
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep == 2)
                StopMicTest();

            if (_currentStep > 1)
            {
                _currentStep--;
                UpdateStepVisibility();
            }
        }

        private void BtnFinish_Click(object sender, RoutedEventArgs e)
        {
            _settings.IsFirstRun = false;
            _settings.Save();
            this.DialogResult = true;
            this.Close();
        }

        private void BtnTestMic_Click(object sender, RoutedEventArgs e)
        {
            if (_testRecorder == null)
                StartMicTest();
            else
                StopMicTest();
        }

        private string TryGetResource(string key, string fallback)
        {
            return System.Windows.Application.Current.TryFindResource(key) as string ?? fallback;
        }

        private void StartMicTest()
        {
            if (_testRecorder != null) return;

            BtnTestMic.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, "BtnTestMicStop");
            PrgMicLevel.Value = 0;
            TxtMicPeak.Text = "0%";
            PrgMicLevel.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 205, 50));
            _lastMicPeakTime = DateTime.MinValue;

            _testRecorder = new AudioRecorder();
            _testRecorder.PeakAvailable += TestRecorder_PeakAvailable;

            string? micId = (CmbMicrophones.SelectedItem as ComboBoxItem)?.Tag as string;
            string tempWav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "whisper_test_wizard.wav");
            
            _testRecorder.StartRecording(micId ?? "", tempWav);
        }

        private void StopMicTest()
        {
            if (_testRecorder == null) return;
            _testRecorder.PeakAvailable -= TestRecorder_PeakAvailable;
            _testRecorder.StopRecording();
            _testRecorder.Dispose();
            _testRecorder = null;

            BtnTestMic.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, "BtnTestMicStart");
            PrgMicLevel.Value = 0;
            TxtMicPeak.Text = "0%";
            PrgMicLevel.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 205, 50));
        }

        private void TestRecorder_PeakAvailable(double peak)
        {
            Dispatcher.InvokeAsync(() =>
            {
                double val = Math.Min(100.0, peak);
                PrgMicLevel.Value = val;
                TxtMicPeak.Text = $"{val:F0}%";
                
                if (val >= 98.0)
                {
                    _lastMicPeakTime = DateTime.UtcNow;
                    PrgMicLevel.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54)); // Red
                }
                else if ((DateTime.UtcNow - _lastMicPeakTime).TotalMilliseconds > 500)
                {
                    PrgMicLevel.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 205, 50)); // LimeGreen
                }
            });
        }

        private void RunModelAdvisor()
        {
            long totalRamMb = 0;
            try { totalRamMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024; } catch { }

            string hardwareNote = TryGetResource("WizardHardwareNote", "Note: Transcription speed depends heavily on your GPU's Video RAM (VRAM). Choose a larger model if you have a dedicated GPU.");
            string info = $"System RAM: {totalRamMb / 1024} GB\n\n{hardwareNote}";

            TxtHardwareInfo.Text = info;

            string recommended = "ggml-base.bin (Default)";
            if (totalRamMb > 12000)
            {
                recommended = "ggml-large-v3.bin (Best Quality if >6GB VRAM)";
            }
            else if (totalRamMb > 6000)
            {
                recommended = "ggml-small.bin (Balanced if >3GB VRAM)";
            }

            TxtRecommendedModel.Text = TryGetResource("WizardAdvisorModelRec", "Recommended Model: ") + "\n" + recommended;
        }

        protected override void OnClosed(EventArgs e)
        {
            StopMicTest();
            base.OnClosed(e);
        }
    }
}
