using NHotkey.Wpf;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WhisperVoice.Services;

namespace WhisperVoice
{
    public class TranscriptionEntry
    {
        public string Text { get; set; } = "";
        public string TimeLabel { get; set; } = "";
        public string Lang { get; set; } = "";
        public bool IsTranslate { get; set; }

        public string ShortText =>
            Text.Length > 120 ? Text[..117] + "..." : Text;

        public string Badge => IsTranslate ? "→EN" : Lang.ToUpper();
    }

    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private const int VK_CONTROL = 0x11;

        private string BaseDir => AppDomain.CurrentDomain.BaseDirectory;
        private string AppDataDir => AppSettings.AppDataDir;
        private string DictDir => Path.Combine(AppDataDir, "dictionary");
        private string TempWavPath => Path.Combine(Path.GetTempPath(), "WhisperVoice_temp.wav");
        private string TempTxtPath => Path.Combine(Path.GetTempPath(), "WhisperVoice_temp.wav.txt");
        private string LogPath => Path.Combine(AppDataDir, "whisper_debug.log");
        private string DictPath => Path.Combine(DictDir, "dictionary.txt");

        private readonly AudioCaptureService _microphoneCapture;
        private readonly AudioCaptureService _loopbackCapture;
        private AudioCaptureService _activeCapture;

        private readonly WhisperExecutionService _whisper;
        private readonly HardwareCheckService _hardware;
        private readonly HallucinationFilter _hallucinationFilter;

        private AppSettings _settings = AppSettings.Load();

        private RecordingOrchestrationService _recorder = null!;

        private readonly ITrayIconService _trayIconService = new TrayIconService();
        private readonly IClipboardService _clipboardService = new ClipboardService();
        private readonly IHistoryService _historyService = new HistoryService();

        private readonly NotepadWindow _notepad = new();
        private readonly PromptWindow _promptWindow = new();
        private SettingsWindow _settingsWindow = new();

        // ── Recording state (owned by RecordingOrchestrationService) ───────
        private CancellationTokenSource? _whisperCts;

        private DoubleAnimation? _vadAnim;

        private readonly TextPostProcessorService _postProcessor = new();

        private HotkeyOrchestrationService? _hotkeyOrchestrator;

        private DateTime _lastAction = DateTime.MinValue;

        public MainWindow()
        {
            App.ApplyInterfaceLanguage(AppSettings.Load().AppInterfaceLanguage);

            InitializeComponent();

            _ = DiagnosticLogger.Instance;
            _whisper = new WhisperExecutionService();
            _hardware = new HardwareCheckService();
            _hallucinationFilter = new HallucinationFilter(DictDir);

            _microphoneCapture = new AudioCaptureService(loopbackMode: false);
            _loopbackCapture = new AudioCaptureService(loopbackMode: true);
            _activeCapture = _microphoneCapture;

            _recorder = new Services.RecordingOrchestrationService(
                _microphoneCapture,
                _loopbackCapture,
                _whisper,
                _hardware,
                _hallucinationFilter,
                _postProcessor,
                TempWavPath);

            _recorder.StateChanged           += Recorder_StateChanged;
            _recorder.TranscriptionCompleted += Recorder_TranscriptionCompleted;
            _recorder.StatusUpdated          += (_, msg) => Dispatcher.InvokeAsync(() => LblStatus.Text = msg);
            _recorder.RecordingTimerTick      += Recorder_TimerTick;
            _recorder.MissingModelRequested   += (_, _) => Dispatcher.InvokeAsync(() => { Show(); new MissingModelWindow { Owner = this }.ShowDialog(); });
            _recorder.ErrorOccurred           += (_, key) => ShowErrorPopup(key);
            _recorder.VulkanStatusChecked    += Recorder_VulkanStatusChecked;

            ClearLogs();
            CleanupTempFiles();
            SetupTrayIcon();

            WireAudioEvents(_microphoneCapture);
            WireAudioEvents(_loopbackCapture);

            HistoryList.ItemsSource = _historyService.Entries;

            LoadMicFromSettings();
            UpdateLanguageButton();
            UpdateTopBar();

            IsVisibleChanged += (_, _) => { if (IsVisible) SyncVolumeFromSystem(); };
            System.Windows.Application.Current.Exit += (_, _) => FullShutdown();
        }

        private void WireAudioEvents(AudioCaptureService service)
        {
            service.PeakAvailable += val => Dispatcher.InvokeAsync(() => {
                if (_activeCapture == service) VuMeter.Value = val;
            });

            service.SilenceDetected += () => Dispatcher.InvokeAsync(() => {
                if (_activeCapture == service) OnVadSilenceDetected();
            });

            service.RecordingAborted += OnRecordingAborted;

            if (service == _microphoneCapture)
            {
                service.VolumeChanged += vol => Dispatcher.Invoke(() =>
                {
                    SldVolume.ValueChanged -= SldVolume_ValueChanged;
                    SldVolume.Value = vol * 100;
                    SldVolume.ValueChanged += SldVolume_ValueChanged;
                });
                service.DeviceDisconnected += OnDeviceDisconnected;
            }
        }

        private void OnRecordingAborted(Exception ex)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                if (_recorder.IsRecording) await StopAndProcessAsync();
                ShowErrorPopup("ErrRecordingAborted", ex.Message);
            });
        }

        private DateTime _lastDeviceChange = DateTime.MinValue;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(HwndHook);

            // Ensure HWND and WPF message pump are fully ready before registering global hotkeys
            RebindHotkeys();
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_DEVICECHANGE = 0x0219;

            if (msg == WM_DEVICECHANGE)
            {
                if ((DateTime.Now - _lastDeviceChange).TotalMilliseconds > 1500)
                {
                    _lastDeviceChange = DateTime.Now;

                    if (!_microphoneCapture.IsDeviceAttached && !string.IsNullOrEmpty(_settings.MicId))
                    {
                        Task.Run(async () =>
                        {
                            await Task.Delay(2000);

                            Dispatcher.Invoke(() =>
                            {
                                string? newId = FindDeviceIdByName(_settings.MicName);

                                if (newId != null)
                                {
                                    bool success = _microphoneCapture.AttachDevice(newId);

                                    if (success)
                                    {
                                        if (newId != _settings.MicId)
                                        {
                                            _settings.MicId = newId;
                                            _settings.Save();
                                        }

                                        UpdateMicLabel(_settings.MicName, ok: true);
                                        SetupVolumeSlider();
                                        _microphoneCapture.RestartSilentCapture();
                                    }
                                }
                            });
                        });
                    }
                }
            }
            return IntPtr.Zero;
        }

        private string? FindDeviceIdByName(string targetName)
        {
            if (string.IsNullOrEmpty(targetName))
                return null;

            try
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(
                    NAudio.CoreAudioApi.DataFlow.Capture,
                    NAudio.CoreAudioApi.DeviceState.Active);

                foreach (var device in devices)
                {
                    if (device.FriendlyName == targetName)
                        return device.ID;
                }
            }
            catch (Exception ex) { DiagnosticLogger.Instance.Warn("MainWindow", $"Operation failed: {ex.Message}"); }

            return null;
        }

        private void SetupTrayIcon()
        {
            _trayIconService.Initialize(
                Path.Combine(BaseDir, "WhisperVoice.ico"),
                "Whisper Voice",
                TryGetResource("TrayMenuControl", "Control Panel"),
                TryGetResource("TrayMenuNotepad", "Notepad"),
                TryGetResource("TrayMenuExit", "Exit")
            );
            
            _trayIconService.OnRestoreRequested += (_, _) => { Show(); Activate(); };
            _trayIconService.OnNotepadRequested += (_, _) => ToggleWindow(_notepad);
            _trayIconService.OnExitRequested += (_, _) =>
            {
                FullShutdown();
                System.Windows.Application.Current.Shutdown();
            };
        }

        private static void ToggleWindow(Window w)
        {
            if (w.IsVisible) w.Hide(); else { w.Show(); w.Activate(); }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        private void FullShutdown()
        {
            try
            {
                _recorder?.Dispose();
                _microphoneCapture?.Dispose();
                _loopbackCapture?.Dispose();
                _hotkeyOrchestrator?.Dispose();
                _trayIconService?.Dispose();
                CleanupTempFiles();
            }
            catch (Exception ex) { DiagnosticLogger.Instance.Warn("MainWindow", $"Operation failed: {ex.Message}"); }
        }

        // ══════════════════════════════════════════════════════════════════
        // Hotkeys  — delegates to HotkeyOrchestrationService
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Single entry-point for all hotkey registration. Safe to call multiple times
        /// (e.g. on startup, after settings save). Tears down the previous mode and
        /// registers the correct one based on AppSettings.IsPushToTalkEnabled.
        /// Must be called on the UI thread.
        /// </summary>
                        private void RebindHotkeys()
        {
            _settings = AppSettings.Load();

            if (_hotkeyOrchestrator == null)
            {
                _hotkeyOrchestrator = new Services.HotkeyOrchestrationService();
                _hotkeyOrchestrator.OnRecordRequested += HotkeyOrchestrator_OnRecordRequested;
                _hotkeyOrchestrator.OnRecordStopped += HotkeyOrchestrator_OnRecordStopped;
                _hotkeyOrchestrator.OnToggleMenu += (s, e) => { if (!IsSpam()) { if (IsVisible) Hide(); else { Show(); Activate(); } } };
                _hotkeyOrchestrator.OnOpenNotepad += (s, e) => { if (!IsSpam()) ToggleWindow(_notepad); };
            }

            _hotkeyOrchestrator.RebindHotkeys(_settings);
        }

        private void HotkeyOrchestrator_OnRecordRequested(object? sender, Services.HotkeyRequestedEventArgs e)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                await _recorder.HandleHotkeyTrigger(e.Mode, e.Source, _settings.IsPushToTalkEnabled, isKeyDown: true, AppSettings.Load(), key => TryGetResource(key, key));
            });
        }

        private void HotkeyOrchestrator_OnRecordStopped(object? sender, Services.HotkeyRequestedEventArgs e)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                await _recorder.HandleHotkeyTrigger(e.Mode, e.Source, _settings.IsPushToTalkEnabled, isKeyDown: false, AppSettings.Load(), key => TryGetResource(key, key));
            });
        }

        // Kept for legacy call-sites only; actual logic lives in RecordingOrchestrationService.
        private void StartMatrixRecording(Services.ProcessingMode mode, Services.AudioSource source)
        {
            _settings = AppSettings.Load();
            _recorder.StartRecording(new Services.RecordingRequest(mode, source));
        }

        private async void OnVadSilenceDetected()
        {
            if (_recorder.IsRecording)
            {
                WriteLog("VAD: silence threshold reached — auto-stopping.");
                await _recorder.StopAndProcessAsync(AppSettings.Load(), key => TryGetResource(key, key));
            }
        }

        // Thin UI-thread wrapper; orchestration lives in RecordingOrchestrationService.
        private async Task StopAndProcessAsync()
        {
            await _recorder.StopAndProcessAsync(AppSettings.Load(), key => TryGetResource(key, key));
        }

        private void WriteLog(string msg) => DiagnosticLogger.Instance.Info("MainWindow", msg);

        private void Recorder_VulkanStatusChecked(object? sender, Services.VulkanStatus status)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (status == Services.VulkanStatus.CpuFallback)
                {
                    LblStatus.Text = TryGetResource("MsgVulkanCpuFallback", "Warning: Inference is running on CPU. Check Vulkan support.");
                }
            });
        }

        // IsAudioWorthProcessing moved to RecordingOrchestrationService.



        private bool IsSpam()
        {
            var diff = (DateTime.Now - _lastAction).TotalMilliseconds;
            _lastAction = DateTime.Now;
            return diff < 600;
        }

        // ── Whisper orchestration ─────────────────────────────────────────
        // Logic moved to RecordingOrchestrationService.RunWhisperPipelineAsync.

        private async void HistoryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (HistoryList.SelectedItem is not TranscriptionEntry entry) return;
            try { System.Windows.Clipboard.SetText(entry.Text); ShowCopyFeedback(); } catch (Exception ex) { DiagnosticLogger.Instance.Warn("MainWindow", $"Operation failed: {ex.Message}"); }
            await Task.Delay(200);
            HistoryList.SelectedItem = null;
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e) => _historyService.Clear();

        private void BtnExportHistory_Click(object sender, RoutedEventArgs e) => _historyService.PromptExport(AppDataDir);

        private void BtnDiagLog_Click(object sender, RoutedEventArgs e)
        {
            string logPath = DiagnosticLogger.Instance.LogPath;
            if (!File.Exists(logPath)) return;
            try { Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = $"\"{logPath}\"", UseShellExecute = false }); }
            catch (Exception ex) { DiagnosticLogger.Instance.Warn("MainWindow", $"Operation failed: {ex.Message}"); }
        }

        private async void ShowCopyFeedback()
        {
            CopyFeedback.Visibility = Visibility.Visible;
            await Task.Delay(1200);
            CopyFeedback.Visibility = Visibility.Collapsed;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _recorder.CancelWhisper();
            LblStatus.Text = TryGetResource("LblCancelled", "Cancelled");
        }

        private void LoadMicFromSettings()
        {
            if (_settings.HasMic)
            {
                bool attached = _microphoneCapture.AttachDevice(_settings.MicId);
                UpdateMicLabel(_settings.MicName, ok: attached);
                if (attached) SetupVolumeSlider();
            }
            else
            {
                UpdateMicLabel((string)FindResource("LblNoMicSelected"), ok: false);
            }
        }

        private void SetupVolumeSlider()
        {
            SldVolume.ValueChanged -= SldVolume_ValueChanged;
            SldVolume.Value = _microphoneCapture.GetVolume() * 100;
            SldVolume.ValueChanged += SldVolume_ValueChanged;
            VolumePanel.Visibility = Visibility.Visible;
        }

        private void SyncVolumeFromSystem()
        {
            SldVolume.ValueChanged -= SldVolume_ValueChanged;
            SldVolume.Value = _microphoneCapture.GetVolume() * 100;
            SldVolume.ValueChanged += SldVolume_ValueChanged;
        }

        private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => _microphoneCapture.SetVolume((float)(SldVolume.Value / 100.0));

        private void UpdateMicLabel(string text, bool ok)
        {
            LblMicName.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            if (!ok)
            {
                LblMicName.Text = text;
                LblMicName.Foreground = System.Windows.Media.Brushes.Red;
            }
            if (LblSelectedDeviceName != null) LblSelectedDeviceName.Text = text;
            VolumePanel.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnSelectMic_Click(object sender, RoutedEventArgs e)
        {
            var mic = new MicWindow { Owner = this };
            if (mic.ShowDialog() == true)
            {
                _settings.MicId = mic.SelectedMicId;
                _settings.MicName = mic.SelectedMicName;
                _settings.Save();
                _microphoneCapture.AttachDevice(_settings.MicId);
                UpdateMicLabel(_settings.MicName, ok: true);
                SetupVolumeSlider();
            }
        }

        private void UpdateLanguageButton(string? activeKey = null)
        {
            _settings = AppSettings.Load();
            string langKey = _settings.LanguagePrimary switch
            {
                "en" => "LangNameEn",
                "uk" => "LangNameUk",
                "pl" => "LangNamePl",
                "de" => "LangNameDe",
                "es" => "LangNameEs",
                "fr" => "LangNameFr",
                _ => "LangNameRu"
            };
            string langName = TryGetResource(langKey, _settings.LanguagePrimary.ToUpper());
            if (LblCurrentLanguage != null)
                LblCurrentLanguage.Text = string.IsNullOrEmpty(activeKey) ? $"🌐 {langName}" : $"🌐 {langName} [{activeKey}]";
        }

        private void UpdateTopBar()
        {
            _settings = AppSettings.Load();
            
            string defaultModeText = _settings.IsPushToTalkEnabled ? "Push-To-Talk" : "Toggle";
            if (LblHotkeyMode != null)
            {
                LblHotkeyMode.Text = TryGetResource(_settings.IsPushToTalkEnabled ? "ModePushToTalk" : "ModeToggle", defaultModeText);
            }

            if (IconPrivacy != null)
            {
                IconPrivacy.ToolTip = TryGetResource("ToolTipPrivacyPolicy", 
                    "Audio is temporarily stored locally before transcription and deleted upon exit.");
            }
        }

        private void BtnLanguageSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow.IsVisible) { _settingsWindow.Activate(); return; }
            _settingsWindow = new SettingsWindow { Owner = this };
            _settingsWindow.Closed += (_, _) => { UpdateLanguageButton(); RebindHotkeys(); UpdateTopBar(); };
            _settingsWindow.Show();
        }

        private void StartVadAnimation()
        {
            _vadAnim ??= new DoubleAnimation { From = 1.0, To = 0.1, Duration = TimeSpan.FromSeconds(0.75), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            VadDot.BeginAnimation(UIElement.OpacityProperty, _vadAnim);
            VadPanel.Visibility = Visibility.Visible;
        }

        private void StopVadAnimation()
        {
            VadDot.BeginAnimation(UIElement.OpacityProperty, null);
            VadDot.Opacity = 0;
            VadPanel.Visibility = Visibility.Collapsed;
        }

        // Timer tick handling moved to Recorder_TimerTick event handler.

        private void ShowProcessingPanel(bool show)
        {
            ProcessingPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) LblStatus.Text = "";
        }

        private void BtnSound_Click(object sender, RoutedEventArgs e)
            => Process.Start(new ProcessStartInfo { FileName = "rundll32.exe", Arguments = "shell32.dll,Control_RunDLL mmsys.cpl,,1", UseShellExecute = true });

        private void BtnPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (_promptWindow.IsVisible) _promptWindow.Hide();
            else { _promptWindow.LoadTags(); _promptWindow.Show(); _promptWindow.Activate(); }
        }

        private void BtnOpenNotepad_Click(object sender, RoutedEventArgs e) => ToggleWindow(_notepad);

        // LoadDictPrompt moved to RecordingOrchestrationService.

        private void ClearLogs() { try { if (File.Exists(LogPath)) File.Delete(LogPath); } catch (Exception ex) { DiagnosticLogger.Instance.Warn("MainWindow", $"Operation failed: {ex.Message}"); } }

        private void CleanupTempFiles()
        {
            TransientDataCleaner.Cleanup(
                TempWavPath, 
                TempTxtPath, 
                Path.Combine(BaseDir, "models"),
                onError: (msg, ex) => DiagnosticLogger.Instance.Warn("MainWindow", $"{msg}: {ex.Message}"),
                onInfo: msg => DiagnosticLogger.Instance.Info("MainWindow", msg));
        }

        private void ShowErrorPopup(string resourceKey, string? details = null)
        {
            Dispatcher.InvokeAsync(() =>
            {
                string message = TryGetResource(resourceKey, resourceKey);
                if (details != null) message += $"\n\nDetails: {details}";
                string title = TryGetResource("MsgErrorTitle", "Error");
                System.Windows.MessageBox.Show(this, message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            });
        }

        private string TryGetResource(string key, string fallback)
        {
            try { return (string)FindResource(key); } catch { return fallback; }
        }

        private void OnDeviceDisconnected()
        {
            Dispatcher.InvokeAsync(async () =>
            {
                if (_recorder.IsRecording) await StopAndProcessAsync();
                ShowErrorPopup("ErrMicUnplugged");
                UpdateMicLabel(TryGetResource("LblNoMicSelected", "⚠️ SELECT A MICROPHONE!"), ok: false);
            });
        }

        // ── RecordingOrchestrationService event handlers ──────────────────

        private void Recorder_StateChanged(object? sender, Services.RecordingStateChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                switch (e.State)
                {
                    case Services.RecordingState.Recording:
                        StartVadAnimation();
                        string keyBase = e.Mode switch
                        {
                            Services.ProcessingMode.Primary   => _settings.HotkeyPrimary,
                            Services.ProcessingMode.Translate => _settings.HotkeyTranslate,
                            _                                  => _settings.HotkeyPrompt
                        };
                        string keySig = e.Source == Services.AudioSource.Loopback
                            ? "Ctrl+" + keyBase : keyBase;
                        UpdateLanguageButton(keySig);
                        LblMicName.Visibility  = Visibility.Visible;
                        LblMicName.Text        = TryGetResource("LblRecording", "Recording") + " 0:00";
                        LblMicName.Foreground  = System.Windows.Media.Brushes.Red;
                        
                        BtnActionRecord.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, "BtnHeroRecording");
                        BtnActionRecord.Background = System.Windows.Media.Brushes.Crimson;
                        BtnActionRecord.IsEnabled = true;
                        break;

                    case Services.RecordingState.Processing:
                        StopVadAnimation();
                        LblMicName.Text       = TryGetResource("LblProcessing", "Processing…");
                        LblMicName.Foreground = System.Windows.Media.Brushes.Orange;
                        UpdateLanguageButton();
                        VuMeter.Value = 0;
                        ShowProcessingPanel(true);
                        
                        BtnActionRecord.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, "BtnHeroProcessing");
                        BtnActionRecord.IsEnabled = false;
                        BtnActionRecord.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#3A3A3A");
                        BtnActionRecord.Foreground = System.Windows.Media.Brushes.Orange;
                        break;

                    case Services.RecordingState.Idle:
                        StopVadAnimation();
                        ShowProcessingPanel(false);
                        UpdateMicLabel(_settings.MicName, ok: true);
                        
                        BtnActionRecord.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, "BtnHeroIdle");
                        BtnActionRecord.IsEnabled = true;
                        BtnActionRecord.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#1565C0");
                        BtnActionRecord.Foreground = System.Windows.Media.Brushes.White;
                        break;
                }
            });
        }

        private void Recorder_TranscriptionCompleted(object? sender, Services.TranscriptionResultEventArgs e)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                _historyService.AddEntry(e.Text, e.Lang, e.IsTranslate);

                _settings = AppSettings.Load();
                if (_settings.AutoClipboardCopy)
                {
                    await _clipboardService.CopyAndPasteAsync(e.Text, injectPaste: true);
                }
            });
        }

        private void Recorder_TimerTick(object? sender, int seconds)
        {
            Dispatcher.InvokeAsync(() =>
            {
                int m = seconds / 60;
                int s = seconds % 60;
                string recLabel = TryGetResource("LblRecording", "Recording");
                LblMicName.Text = $"{recLabel} {m}:{s:D2}";
            });
        }

        private async void BtnActionRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_recorder.IsRecording)
            {
                await StopAndProcessAsync();
            }
            else if (!_recorder.IsProcessing)
            {
                StartMatrixRecording(Services.ProcessingMode.Primary, Services.AudioSource.Microphone);
            }
        }
    }
}
