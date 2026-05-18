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
using WindowsInput;
using WindowsInput.Native;

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
        private string LogPath => Path.Combine(AppDataDir, "whisper_debug.log");
        private string DictPath => Path.Combine(DictDir, "dictionary.txt");

        private readonly AudioCaptureService _microphoneCapture;
        private readonly AudioCaptureService _loopbackCapture;
        private AudioCaptureService _activeCapture;

        private readonly WhisperExecutionService _whisper;
        private readonly HardwareCheckService _hardware;
        private readonly HallucinationFilter _hallucinationFilter;

        private AppSettings _settings = AppSettings.Load();

        private System.Windows.Forms.NotifyIcon _trayIcon = null!;
        private readonly InputSimulator _inputSim = new();

        private readonly NotepadWindow _notepad = new();
        private readonly PromptWindow _promptWindow = new();
        private SettingsWindow _settingsWindow = new();

        // ── Recording state ────────────────────────────────────────────────
        private NAudio.Wave.WasapiLoopbackCapture? _loopbackCapture;
        private NAudio.Wave.WaveFileWriter? _loopbackWriter;
        private bool _isLoopbackActive = false;

        private enum RecordMode { None, Primary, Translate, Prompt }
        private RecordMode _activeMode = RecordMode.None;

        private string _currentLang = "ru";
        private bool _currentTranslate = false;
        private bool _isProcessing = false;
        private int _stopGuard = 0;

        private CancellationTokenSource? _whisperCts;

        private const int MaxHistory = 10;
        private readonly ObservableCollection<TranscriptionEntry> _history = new();

        private DoubleAnimation? _vadAnim;

        private DispatcherTimer? _recTimer;
        private int _recSeconds = 0;

        private readonly TextPostProcessorService _postProcessor = new();
        private readonly HistoryExportService _historyExport = new();

        private HotkeyOrchestrationService? _hotkeyOrchestrator;

        private DateTime _lastAction = DateTime.MinValue;

        public MainWindow()
        {
            App.ApplyInterfaceLanguage(AppSettings.Load().AppInterfaceLanguage);

            InitializeComponent();

            _ = DiagnosticLogger.Instance;
            _whisper = new WhisperExecutionService(BaseDir);
            _hardware = new HardwareCheckService();
            _hallucinationFilter = new HallucinationFilter(DictDir);

            _microphoneCapture = new AudioCaptureService(loopbackMode: false);
            _loopbackCapture = new AudioCaptureService(loopbackMode: true);
            _activeCapture = _microphoneCapture;

            ClearLogs();
            CleanupTempFiles();
            SetupTrayIcon();

            WireAudioEvents(_microphoneCapture);
            WireAudioEvents(_loopbackCapture);

            HistoryList.ItemsSource = _history;

            LoadMicFromSettings();
            RebindHotkeys();
            UpdateLanguageButton();

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
                if (_activeMode != RecordMode.None) await StopAndProcessAsync();
                ShowErrorPopup("ErrRecordingAborted");
            });
        }

        private DateTime _lastDeviceChange = DateTime.MinValue;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(HwndHook);
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
            catch { }

            return null;
        }

        private void SetupTrayIcon()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = new System.Drawing.Icon(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WhisperVoice.ico")),
                Visible = true,
                Text = "Whisper Voice"
            };
            _trayIcon.DoubleClick += (_, _) => { Show(); Activate(); };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add((string)FindResource("TrayMenuControl"), null, (_, _) => { Show(); Activate(); });
            menu.Items.Add((string)FindResource("TrayMenuNotepad"), null, (_, _) => ToggleWindow(_notepad));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add((string)FindResource("TrayMenuExit"), null, (_, _) =>
            {
                FullShutdown();
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                System.Windows.Application.Current.Shutdown();
            });
            _trayIcon.ContextMenuStrip = menu;
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
                _whisperCts?.Cancel();
                _microphoneCapture?.Dispose();
                _loopbackCapture?.Dispose();
                _hotkeyOrchestrator?.Dispose();
                CleanupTempFiles();
            }
            catch { }
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
                if (_isProcessing) return;

                if (_settings.IsPushToTalkEnabled)
                {
                    if (!_audio.IsRecording && !_isLoopbackActive)
                    {
                        StartMatrixRecording(e.Mode, e.Source);
                    }
                }
                else
                {
                    if (!_audio.IsRecording && !_isLoopbackActive)
                    {
                        StartMatrixRecording(e.Mode, e.Source);
                    }
                    else
                    {
                        if (_activeMode == (RecordMode)e.Mode)
                        {
                            await StopAndProcessAsync();
                        }
                    }
                }
            });
        }

        private void HotkeyOrchestrator_OnRecordStopped(object? sender, Services.HotkeyRequestedEventArgs e)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                if (_settings.IsPushToTalkEnabled && (_audio.IsRecording || _isLoopbackActive))
                {
                    await StopAndProcessAsync();
                }
            });
        }

        private void StartMatrixRecording(Services.ProcessingMode mode, Services.AudioSource source)
        {
            if (string.IsNullOrEmpty(_settings.MicId) && source == Services.AudioSource.Microphone) { Show(); return; }

            if (string.IsNullOrEmpty(_settings.LastModelPath) || !File.Exists(_settings.LastModelPath))
            {
                Show();
                new MissingModelWindow { Owner = this }.ShowDialog();
                return;
            }

            _activeMode = (RecordMode)mode;
            _currentTranslate = (mode == Services.ProcessingMode.Translate || mode == Services.ProcessingMode.Prompt);
            _currentLang = (mode == Services.ProcessingMode.Primary) ? _settings.LanguagePrimary : "en";

            if (File.Exists(TempWavPath)) File.Delete(TempWavPath);

            string keyBase = mode switch {
                Services.ProcessingMode.Primary => _settings.HotkeyPrimary,
                Services.ProcessingMode.Translate => _settings.HotkeyTranslate,
                _ => _settings.HotkeyPrompt
            };
            string keySignature = source == Services.AudioSource.Loopback ? "Ctrl+" + keyBase : keyBase;

            if (source == Services.AudioSource.Loopback)
            {
                _isLoopbackActive = true;
                string rawLoopWav = TempWavPath + ".raw.wav";
                if (File.Exists(rawLoopWav)) File.Delete(rawLoopWav);

                try
                {
                    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    var renderDevice = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                    
                    _loopbackCapture = new NAudio.Wave.WasapiLoopbackCapture(renderDevice);
                    _loopbackWriter = new NAudio.Wave.WaveFileWriter(rawLoopWav, _loopbackCapture.WaveFormat);

                    _loopbackCapture.DataAvailable += (s, a) => {
                        if (a.BytesRecorded > 0 && _loopbackWriter != null)
                            _loopbackWriter.Write(a.Buffer, 0, a.BytesRecorded);
                    };

                    _loopbackCapture.RecordingStopped += (s, a) => {
                        _loopbackWriter?.Dispose(); _loopbackWriter = null;
                        _loopbackCapture?.Dispose(); _loopbackCapture = null;
                    };

                    _loopbackCapture.StartRecording();
                }
                catch (Exception ex)
                {
                    WriteLog("Failed to initialize WASAPI loopback engine: " + ex.Message);
                    _isLoopbackActive = false;
                    return;
                }
            }
            else
            {
                bool started = _audio.StartRecording(_settings.MicId, TempWavPath, _settings.VadThreshold, _settings.VadSilenceSeconds);
                if (!started) { ShowErrorPopup("ErrMicUnplugged"); return; }
            }

            StartVadAnimation();
            UpdateLanguageButton(keySignature);
            if (_settings.SoundNotifications) SystemSounds.Beep.Play();
            StartRecordingTimer();

            LblMicName.Text = (string)FindResource("LblRecording") + " 0:00";
            LblMicName.Foreground = System.Windows.Media.Brushes.Red;
        }

        private async void OnVadSilenceDetected()
        {
            if ((_audio.IsRecording || _isLoopbackActive) && _activeMode != RecordMode.None)
            {
                WriteLog("VAD: silence threshold reached — auto-stopping.");
                await StopAndProcessAsync();
            }
        }

        private async Task StopAndProcessAsync()
        {
            if (Interlocked.Exchange(ref _stopGuard, 1) != 0) return;
            try
            {
                if (_isLoopbackActive)
                {
                    _isLoopbackActive = false;
                    _loopbackCapture?.StopRecording();
                    await Task.Delay(250);

                    string rawLoopWav = TempWavPath + ".raw.wav";
                    if (File.Exists(rawLoopWav))
                    {
                        try
                        {
                            var targetFormat = new NAudio.Wave.WaveFormat(16000, 1);
                            using (var reader = new NAudio.Wave.WaveFileReader(rawLoopWav))
                            using (var resampler = new NAudio.Wave.MediaFoundationResampler(reader, targetFormat))
                            {
                                NAudio.Wave.WaveFileWriter.CreateWaveFile(TempWavPath, resampler);
                            }
                            File.Delete(rawLoopWav);
                        }
                        catch (Exception ex)
                        {
                            WriteLog("Loopback audio processing resampler failed: " + ex.Message);
                        }
                    }
                }
                else
                {
                    await _audio.StopRecordingAsync();
                }

                StopVadAnimation();
                StopRecordingTimer();

                if (!IsAudioWorthProcessing(TempWavPath))
                {
                    _activeMode = RecordMode.None;
                    _isProcessing = false;
                    UpdateMicLabel(_settings.MicName, ok: true);
                    return;
                }

                var mode = _activeMode;
                var lang = _currentLang;
                var translate = _currentTranslate;
                _activeMode = RecordMode.None;
                _isProcessing = true;

                LblMicName.Text = (string)FindResource("LblProcessing");
                LblMicName.Foreground = System.Windows.Media.Brushes.Orange;
                UpdateLanguageButton();
                VuMeter.Value = 0;
                ShowProcessingPanel(true);

                _whisperCts = new CancellationTokenSource();
                var progress = new Progress<string>(msg => {
                    if (!string.IsNullOrWhiteSpace(msg)) LblStatus.Text = msg;
                });

                string selectedPrompt = mode switch {
                    RecordMode.Translate => _settings.PromptTranslate,
                    RecordMode.Prompt => LoadDictPrompt(),
                    _ => LoadDictPrompt()
                };

                await ProcessWhisperAsync(lang, translate, selectedPrompt, progress, _whisperCts.Token);

                _isProcessing = false;
                ShowProcessingPanel(false);
                UpdateMicLabel(_settings.MicName, ok: true);
            }
            finally
            {
                Interlocked.Exchange(ref _stopGuard, 0);
                // Fix: return active capture to microphone to restore signal level monitoring
                _activeCapture = _microphoneCapture;
            }
        }

        private bool IsSpam()
        {
            var diff = (DateTime.Now - _lastAction).TotalMilliseconds;
            _lastAction = DateTime.Now;
            return diff < 600;
        }

        // ── Whisper orchestration ──────────────────────────────────────────
        private async Task ProcessWhisperAsync(
            string lang, bool isTranslate, string techPrompt,
            IProgress<string> progress, CancellationToken token)
        {
            try
            {
                string ramFmt = await Dispatcher.InvokeAsync(() => TryGetResource("ErrLowRam", "Not enough RAM (need ≥ {0} MB free)."));
                string vramFmt = await Dispatcher.InvokeAsync(() => TryGetResource("ErrLowVram", "VRAM almost full ({0} MB free, need ≥ {1} MB)."));

                var (ramOk, ramMsg) = await _hardware.CheckRamAsync(ramFmt);
                if (!ramOk)
                {
                    await Dispatcher.InvokeAsync(() =>
                        System.Windows.MessageBox.Show(this, ramMsg,
                            (string)FindResource("MsgLowResourcesTitle"),
                            MessageBoxButton.OK, MessageBoxImage.Warning));
                    return;
                }

                var (vramOk, vramMsg) = await _hardware.CheckVramAsync(vramFmt);
                if (!vramOk)
                {
                    var choice = await Dispatcher.InvokeAsync(() =>
                        System.Windows.MessageBox.Show(this,
                            vramMsg + (string)FindResource("MsgVramContinue"),
                            (string)FindResource("MsgVramTitle"),
                            MessageBoxButton.YesNo, MessageBoxImage.Warning));
                    if (choice == MessageBoxResult.No) return;
                }

                string model = await Dispatcher.InvokeAsync(() =>
                {
                    string saved = AppSettings.Load().LastModelPath;
                    return !string.IsNullOrEmpty(saved) ? saved : Path.Combine(BaseDir, "models", "ggml-large-v3.bin");
                });

                string? rawResult = await _whisper.RunAsync(
                    model, lang, isTranslate, techPrompt,
                    progress, (msg) => System.Diagnostics.Debug.WriteLine(msg), token,
                    beamSize: _settings.BeamSize,
                    bestOf: _settings.BestOf,
                    temperature: _settings.Temperature,
                    noSpeechThreshold: _settings.NoSpeechThreshold);

                if (rawResult is null) return;

                if (!_hallucinationFilter.Check(rawResult, out string cleanResult))
                {
                    progress.Report((string)FindResource("MsgHallucinationFiltered"));
                    return;
                }

                string finalResult = _postProcessor.Process(cleanResult);

                progress.Report((string)FindResource("MsgWhisperDone"));

                await Dispatcher.InvokeAsync(async () =>
                {
                    AddToHistory(finalResult, lang, isTranslate);
                    System.Windows.Clipboard.SetText(finalResult);
                    await Task.Delay(100);
                    _inputSim.Keyboard.ModifiedKeyStroke(
                        VirtualKeyCode.CONTROL, VirtualKeyCode.VK_V);
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                    System.Windows.MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error));
            }
        }

        private void AddToHistory(string text, string lang, bool isTranslate)
        {
            _history.Insert(0, new TranscriptionEntry
            {
                Text = text,
                TimeLabel = DateTime.Now.ToString("HH:mm:ss"),
                Lang = lang,
                IsTranslate = isTranslate
            });
            while (_history.Count > MaxHistory)
                _history.RemoveAt(_history.Count - 1);
        }

        private async void HistoryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (HistoryList.SelectedItem is not TranscriptionEntry entry) return;
            try { System.Windows.Clipboard.SetText(entry.Text); ShowCopyFeedback(); } catch { }
            await Task.Delay(200);
            HistoryList.SelectedItem = null;
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e) => _history.Clear();

        private void BtnExportHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_history.Count == 0) return;
            var dialog = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt",
                DefaultExt = "csv",
                FileName = _historyExport.GenerateTimestampedFilename("csv"),
                InitialDirectory = AppDataDir
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                try
                {
                    var entries = _history.Select(e => (e.TimeLabel, e.Text)).ToList();
                    if (dialog.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        _historyExport.ExportToTxt(entries, dialog.FileName);
                    else
                        _historyExport.ExportToCsv(entries, dialog.FileName);
                }
                catch { }
            }
        }

        private void BtnDiagLog_Click(object sender, RoutedEventArgs e)
        {
            string logPath = DiagnosticLogger.Instance.LogPath;
            if (!File.Exists(logPath)) return;
            try { Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = $"\"{logPath}\"", UseShellExecute = false }); }
            catch { }
        }

        private async void ShowCopyFeedback()
        {
            CopyFeedback.Visibility = Visibility.Visible;
            await Task.Delay(1200);
            CopyFeedback.Visibility = Visibility.Collapsed;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _whisperCts?.Cancel();
            LblStatus.Text = (string)FindResource("LblCancelled");
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

        private void BtnLanguageSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow.IsVisible) { _settingsWindow.Activate(); return; }
            _settingsWindow = new SettingsWindow { Owner = this };
            _settingsWindow.Closed += (_, _) => { UpdateLanguageButton(); RebindHotkeys(); };
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

        private void StartRecordingTimer(bool isLoopback)
        {
            _recSeconds = 0;
            _recTimer?.Stop();
            LblMicName.Visibility = Visibility.Visible;

            string prefix = isLoopback ? "🔊" : "🎤";
            string recLabel = TryGetResource("LblRecording", "Recording");

            // Fix: Immediately update label to 0:00 to avoid display jumps
            LblMicName.Text = $"{prefix} {recLabel} 0:00";

            _recTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _recTimer.Tick += (_, _) =>
            {
                _recSeconds++;
                int m = _recSeconds / 60;
                int s = _recSeconds % 60;
                LblMicName.Text = $"{prefix} {recLabel} {m}:{s:D2}";
            };
            _recTimer.Start();
        }

        private void StopRecordingTimer() { _recTimer?.Stop(); _recTimer = null; _recSeconds = 0; }

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

        private string LoadDictPrompt()
        {
            try
            {
                if (!File.Exists(DictPath)) return "";
                string raw = File.ReadAllText(DictPath).Replace("\r\n", " ").Replace("\n", " ").Replace("\"", "");
                return raw.Length > 250 ? raw[..250] : raw;
            }
            catch { return ""; }
        }

        private void ClearLogs() { try { if (File.Exists(LogPath)) File.Delete(LogPath); } catch { } }

        private void CleanupTempFiles()
        {
            try { if (File.Exists(TempWavPath)) File.Delete(TempWavPath); } catch { }
        }

        private void ShowErrorPopup(string resourceKey)
        {
            Dispatcher.InvokeAsync(() =>
            {
                string message = TryGetResource(resourceKey, resourceKey);
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
                if (_activeMode != RecordMode.None) await StopAndProcessAsync();
                ShowErrorPopup("ErrMicUnplugged");
                UpdateMicLabel(TryGetResource("LblNoMicSelected", "⚠️ SELECT A MICROPHONE!"), ok: false);
            });
        }
    }
}
