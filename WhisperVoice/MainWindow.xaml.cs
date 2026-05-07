using NHotkey.Wpf;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
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
    // ── Data models ────────────────────────────────────────────────────────
    public class TranscriptionEntry
    {
        public string Text { get; set; } = "";
        public string TimeLabel { get; set; } = "";
        public string Lang { get; set; } = "";
        public bool IsTranslate { get; set; }

        public string ShortText =>
            Text.Length > 120 ? Text[..117] + "..." : Text;

        /// <summary>Small badge shown in history: "→EN" for translate mode, or e.g. "RU" for transcription.</summary>
        public string Badge => IsTranslate ? "→EN" : Lang.ToUpper();
    }

    // ── MainWindow — View / Orchestrator only ──────────────────────────────
    public partial class MainWindow : Window
    {
        // ── Paths ──────────────────────────────────────────────────────────
        private string BaseDir => AppDomain.CurrentDomain.BaseDirectory;
        private string AppDataDir => AppSettings.AppDataDir;
        private string DictDir => Path.Combine(AppDataDir, "dictionary");
        private string TempWavPath => Path.Combine(Path.GetTempPath(), "WhisperVoice_temp.wav");
        private string LogPath => Path.Combine(AppDataDir, "whisper_debug.log");
        private string DictPath => Path.Combine(DictDir, "dictionary.txt");

        // ── Services ───────────────────────────────────────────────────────
        private readonly AudioCaptureService _audio;
        private readonly WhisperExecutionService _whisper;
        private readonly HardwareCheckService _hardware;
        private readonly HallucinationFilter _hallucinationFilter;

        // ── Settings ───────────────────────────────────────────────────────
        private AppSettings _settings = AppSettings.Load();

        // ── UI helpers ─────────────────────────────────────────────────────
        private System.Windows.Forms.NotifyIcon _trayIcon = null!;
        private readonly InputSimulator _inputSim = new();

        private readonly NotepadWindow _notepad = new();
        private readonly PromptWindow _promptWindow = new();
        private SettingsWindow _settingsWindow = new();

        // ── Recording state ────────────────────────────────────────────────
        private enum RecordMode { None, Primary, Translate }
        private RecordMode _activeMode = RecordMode.None;

        private string _currentLang = "ru";
        private bool _currentTranslate = false;
        private bool _isProcessing = false;
        private int _stopGuard = 0;  // Interlocked double-stop guard

        // ── Async / cancellation ───────────────────────────────────────────
        private CancellationTokenSource? _whisperCts;

        // ── History ────────────────────────────────────────────────────────
        private const int MaxHistory = 10;
        private readonly ObservableCollection<TranscriptionEntry> _history = new();

        // ── VAD animation ──────────────────────────────────────────────────
        private DoubleAnimation? _vadAnim;

        // ── Recording timer ────────────────────────────────────────────────
        private DispatcherTimer? _recTimer;
        private int _recSeconds = 0;

        // ── Post-processor ─────────────────────────────────────────────────
        private readonly TextPostProcessorService _postProcessor = new();

        // ── History export ─────────────────────────────────────────────────
        private readonly HistoryExportService _historyExport = new();

        // ── Anti-spam ──────────────────────────────────────────────────────
        private DateTime _lastAction = DateTime.MinValue;

        // ══════════════════════════════════════════════════════════════════
        // Constructor
        // ══════════════════════════════════════════════════════════════════
        public MainWindow()
        {
            // Apply saved interface language before XAML resources are resolved
            App.ApplyInterfaceLanguage(AppSettings.Load().AppInterfaceLanguage);

            InitializeComponent();

            // Instantiate services
            _audio = new AudioCaptureService();
            _whisper = new WhisperExecutionService(BaseDir);
            _hardware = new HardwareCheckService();
            _hallucinationFilter = new HallucinationFilter(DictDir);

            ClearLogs();
            CleanupTempFiles();
            SetupTrayIcon();

            // Wire audio service events — these fire on background threads
            _audio.PeakAvailable += val => Dispatcher.InvokeAsync(() => VuMeter.Value = val);
            _audio.SilenceDetected += () => Dispatcher.InvokeAsync(OnVadSilenceDetected);
            _audio.VolumeChanged += vol => Dispatcher.Invoke(() =>
            {
                SldVolume.ValueChanged -= SldVolume_ValueChanged;
                SldVolume.Value = vol * 100;
                SldVolume.ValueChanged += SldVolume_ValueChanged;
            });

            _audio.DeviceDisconnected += OnDeviceDisconnected;

            HistoryList.ItemsSource = _history;

            LoadMicFromSettings();
            SetupHotkeys();
            UpdateLanguageButton();

            IsVisibleChanged += (_, _) => { if (IsVisible) SyncVolumeFromSystem(); };
            System.Windows.Application.Current.Exit += (_, _) => FullShutdown();
        }

        // ── Переменная для таймера авто-реконнекта ──
        private DateTime _lastDeviceChange = DateTime.MinValue;

        // ── Системный хук Windows: ловит любые изменения железа (USB/Jack) ──
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

                    System.Diagnostics.Debug.WriteLine($"[USB] WM_DEVICECHANGE detected. IsAttached={_audio.IsDeviceAttached}, SavedMicId={_settings.MicId}");

                    if (!_audio.IsDeviceAttached && !string.IsNullOrEmpty(_settings.MicId))
                    {
                        Task.Run(async () =>
                        {
                            await Task.Delay(2000);

                            Dispatcher.Invoke(() =>
                            {
                                System.Diagnostics.Debug.WriteLine($"[USB] Searching for device by name: {_settings.MicName}");

                                // Find device by friendly name since ID may have changed
                                string? newId = FindDeviceIdByName(_settings.MicName);

                                System.Diagnostics.Debug.WriteLine($"[USB] FindDeviceIdByName result: {newId ?? "NULL"}");

                                if (newId != null)
                                {
                                    bool success = _audio.AttachDevice(newId);

                                    System.Diagnostics.Debug.WriteLine($"[USB] AttachDevice result: {success}");

                                    if (success)
                                    {
                                        // Update ID if it changed
                                        if (newId != _settings.MicId)
                                        {
                                            System.Diagnostics.Debug.WriteLine($"[USB] ID changed: {_settings.MicId} -> {newId}");
                                            _settings.MicId = newId;
                                            _settings.Save();
                                        }

                                        UpdateMicLabel(_settings.MicName, ok: true);
                                        SetupVolumeSlider();

                                        System.Diagnostics.Debug.WriteLine($"[USB] Calling RestartSilentCapture...");
                                        // CRITICAL: Restart silent capture for peak meter
                                        _audio.RestartSilentCapture();
                                        System.Diagnostics.Debug.WriteLine($"[USB] RestartSilentCapture completed");
                                    }
                                }
                                else
                                {
                                    System.Diagnostics.Debug.WriteLine($"[USB] Device not found by name '{_settings.MicName}'");
                                }
                            });
                        });
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[USB] Skipping reconnect (already attached or no saved mic)");
                    }
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>Find device ID by friendly name (handles USB ID changes)</summary>
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

        // ══════════════════════════════════════════════════════════════════
        // Tray icon
        // ══════════════════════════════════════════════════════════════════
        private void SetupTrayIcon()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = new System.Drawing.Icon(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WhisperVoice.ico")), // Custom icon
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

        // ══════════════════════════════════════════════════════════════════
        // Window lifecycle
        // ══════════════════════════════════════════════════════════════════
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
                _audio.Dispose();
                CleanupTempFiles();
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════
        // Hotkeys  — reads HotkeyPrimary / HotkeyTranslate from settings
        // ══════════════════════════════════════════════════════════════════
        private void SetupHotkeys()
        {
            try
            {
                _settings = AppSettings.Load();

                var keyPrimary = (Key)Enum.Parse(typeof(Key), _settings.HotkeyPrimary, ignoreCase: true);
                var keyTranslate = (Key)Enum.Parse(typeof(Key), _settings.HotkeyTranslate, ignoreCase: true);

                HotkeyManager.Current.AddOrReplace("ToggleMenu", Key.F7, ModifierKeys.None, OnToggleMenu);
                HotkeyManager.Current.AddOrReplace("Primary", keyPrimary, ModifierKeys.None, OnRecordPrimary);
                HotkeyManager.Current.AddOrReplace("Translate", keyTranslate, ModifierKeys.None, OnRecordTranslate);
                HotkeyManager.Current.AddOrReplace("TranslateCtrl", Key.F9, ModifierKeys.Control, OnTranslateWithPrompt);
                HotkeyManager.Current.AddOrReplace("Notepad", Key.F7, ModifierKeys.Control, OnOpenNotepad);
            }
            catch (Exception ex) { WriteLog($"Hotkey setup error: {ex.Message}"); }
        }

        private bool IsSpam()
        {
            var diff = (DateTime.Now - _lastAction).TotalMilliseconds;
            _lastAction = DateTime.Now;
            return diff < 600;
        }

        private void OnToggleMenu(object? s, NHotkey.HotkeyEventArgs e)
        { e.Handled = true; if (!IsSpam()) { if (IsVisible) Hide(); else { Show(); Activate(); } } }

        private void OnOpenNotepad(object? s, NHotkey.HotkeyEventArgs e)
        { e.Handled = true; if (!IsSpam()) ToggleWindow(_notepad); }

        /// <summary>Primary hotkey: record in the user's selected language.</summary>
        private void OnRecordPrimary(object? s, NHotkey.HotkeyEventArgs e)
        {
            e.Handled = true;
            if (!IsSpam() && !_isProcessing)
            {
                _settings = AppSettings.Load();
                ToggleRecording(RecordMode.Primary,
                    _settings.LanguagePrimary, _settings.HotkeyPrimary, false);
            }
        }

        /// <summary>Translate hotkey: force English output regardless of selected language.</summary>
        private void OnRecordTranslate(object? s, NHotkey.HotkeyEventArgs e)
        {
            e.Handled = true;
            if (!IsSpam() && !_isProcessing)
            {
                _settings = AppSettings.Load();
                ToggleRecording(RecordMode.Translate,
                    "en", _settings.HotkeyTranslate, false);
            }
        }

        /// <summary>Ctrl+F9: translate with active user prompt.</summary>
        private void OnTranslateWithPrompt(object? s, NHotkey.HotkeyEventArgs e)
        { e.Handled = true; if (!IsSpam() && !_isProcessing) ToggleRecording(RecordMode.Translate, "ru", "Ctrl+F9", true); }

        // ══════════════════════════════════════════════════════════════════
        // Recording toggle
        // ══════════════════════════════════════════════════════════════════
        private async void ToggleRecording(RecordMode mode, string lang, string keyName, bool isTranslate)
        {
            if (string.IsNullOrEmpty(_settings.MicId)) { Show(); return; }

            if (string.IsNullOrEmpty(_settings.LastModelPath) || !File.Exists(_settings.LastModelPath))
            {
                Show();
                new MissingModelWindow { Owner = this }.ShowDialog();
                return;
            }

            if (!_audio.IsRecording)
            {
                _activeMode = mode;
                _currentLang = lang;
                _currentTranslate = isTranslate;

                if (File.Exists(TempWavPath)) File.Delete(TempWavPath);

                // Сервис всё делает сам в фоне. Просто стартуем.

                bool started = _audio.StartRecording(
                    _settings.MicId, TempWavPath,
                    _settings.VadThreshold, _settings.VadSilenceSeconds);

                if (!started)
                {
                    ShowErrorPopup("ErrMicUnplugged");
                    return;
                }

                StartVadAnimation();
                UpdateLanguageButton(keyName);
                if (_settings.SoundNotifications) SystemSounds.Beep.Play();
                StartRecordingTimer();

                LblMicName.Text = $"{(string)FindResource("LblRecording")} 0:00";
                LblMicName.Foreground = System.Windows.Media.Brushes.Red;
            }
            else
            {
                if (_activeMode != mode) return;
                await StopAndProcessAsync();
            }
        }

        private async void OnVadSilenceDetected()
        {
            if (!_audio.IsRecording || _activeMode == RecordMode.None) return;
            WriteLog("VAD: silence threshold reached — auto-stopping.");
            await StopAndProcessAsync();
        }

        private async Task StopAndProcessAsync()
        {
            if (Interlocked.Exchange(ref _stopGuard, 1) != 0) return;
            try
            {
                await _audio.StopRecordingAsync();
                StopVadAnimation();
                StopRecordingTimer();
                if (_settings.SoundNotifications) SystemSounds.Exclamation.Play();

                var lang = _currentLang;
                var translate = _currentTranslate;
                _activeMode = RecordMode.None;
                _isProcessing = true;

                LblMicName.Text = (string)FindResource("LblProcessing");
                LblMicName.Foreground = System.Windows.Media.Brushes.Orange;
                UpdateLanguageButton(); // strip hotkey indicator
                VuMeter.Value = 0;
                ShowProcessingPanel(true);

                _whisperCts = new CancellationTokenSource();
                var progress = new Progress<string>(msg =>
                {
                    if (!string.IsNullOrWhiteSpace(msg))
                        LblStatus.Text = msg;
                });

                await ProcessWhisperAsync(lang, translate, translate ? _settings.PromptTranslate : LoadDictPrompt(), progress, _whisperCts.Token);

                _isProcessing = false;
                ShowProcessingPanel(false);
                UpdateMicLabel(_settings.MicName, ok: true);
            }
            finally
            {
                Interlocked.Exchange(ref _stopGuard, 0);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Whisper orchestration
        // ══════════════════════════════════════════════════════════════════
        private async Task ProcessWhisperAsync(
            string lang, bool isTranslate, string techPrompt,
            IProgress<string> progress, CancellationToken token)
        {
            try
            {
                // ── Resource pre-checks ────────────────────────────────────
                string ramFmt  = await Dispatcher.InvokeAsync(() => TryGetResource("ErrLowRam",  "Not enough RAM (need ≥ {0} MB free)."));
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

                // ── Model path ─────────────────────────────────────────────
                string model = await Dispatcher.InvokeAsync(() =>
                {
                    string saved = AppSettings.Load().LastModelPath;
                    return !string.IsNullOrEmpty(saved)
                        ? saved
                        : Path.Combine(BaseDir, "models", "ggml-large-v3.bin");
                });

                // ── Run whisper-cli.exe ────────────────────────────────────
                string? rawResult = await _whisper.RunAsync(
                    model, lang, isTranslate, techPrompt,
                    progress, WriteLog, token,
                    beamSize:          _settings.BeamSize,
                    bestOf:            _settings.BestOf,
                    temperature:       _settings.Temperature,
                    noSpeechThreshold: _settings.NoSpeechThreshold);

                if (rawResult is null) return;   // cancelled or file not found

                WriteLog($"Raw result: {rawResult}");

                // ── Hallucination filter ───────────────────────────────────
                if (!_hallucinationFilter.Check(rawResult, out string cleanResult))
                {
                    WriteLog($"Hallucination filtered: {rawResult}");
                    progress.Report((string)FindResource("MsgHallucinationFiltered"));
                    return;
                }

                // ── Post-processor ─────────────────────────────────────────
                string finalResult = _postProcessor.Process(cleanResult);
                WriteLog($"Post-processed: {finalResult}");

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
            catch (WhisperProcessException ex)
            {
                await Dispatcher.InvokeAsync(() =>
                    System.Windows.MessageBox.Show(this, ex.Message,
                        (string)FindResource("MsgWhisperErrorTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Error));
            }
            catch (OperationCanceledException) { WriteLog("ProcessWhisperAsync cancelled."); }
            catch (Exception ex)
            {
                WriteLog($"ProcessWhisperAsync unhandled: {ex}");
                await Dispatcher.InvokeAsync(() =>
                    System.Windows.MessageBox.Show(this,
                        string.Format(TryGetResource("MsgUnhandledErrorBody", "Error:\n{0}"), ex.Message),
                        (string)FindResource("MsgErrorTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Error));
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // History
        // ══════════════════════════════════════════════════════════════════
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

        private async void HistoryList_SelectionChanged(
            object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (HistoryList.SelectedItem is not TranscriptionEntry entry) return;
            try { System.Windows.Clipboard.SetText(entry.Text); ShowCopyFeedback(); } catch { }
            await Task.Delay(200);
            HistoryList.SelectedItem = null;
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e) =>
            _history.Clear();

        private void BtnExportHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_history.Count == 0)
            {
                ShowErrorPopup("No history to export.");
                return;
            }

            var dialog = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = "csv",
                FileName = _historyExport.GenerateTimestampedFilename("csv"),
                InitialDirectory = AppDataDir
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                try
                {
                    var entries = _history
                        .Select(e => (e.TimeLabel, e.Text))
                        .ToList();

                    if (dialog.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        _historyExport.ExportToTxt(entries, dialog.FileName);
                    else
                        _historyExport.ExportToCsv(entries, dialog.FileName);

                    WriteLog($"History exported to: {dialog.FileName}");
                    ShowInfoPopup($"✓ Exported {_history.Count} entries to {Path.GetFileName(dialog.FileName)}");
                }
                catch (Exception ex)
                {
                    WriteLog($"Export failed: {ex.Message}");
                    ShowErrorPopup($"Export failed: {ex.Message}");
                }
            }
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

        // ══════════════════════════════════════════════════════════════════
        // Mic / volume
        // ══════════════════════════════════════════════════════════════════
        private void LoadMicFromSettings()
        {
            if (_settings.HasMic)
            {
                bool attached = _audio.AttachDevice(_settings.MicId);
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
            SldVolume.Value = _audio.GetVolume() * 100;
            SldVolume.ValueChanged += SldVolume_ValueChanged;
            VolumePanel.Visibility = Visibility.Visible;
        }

        private void SyncVolumeFromSystem()
        {
            SldVolume.ValueChanged -= SldVolume_ValueChanged;
            SldVolume.Value = _audio.GetVolume() * 100;
            SldVolume.ValueChanged += SldVolume_ValueChanged;
        }

        private void SldVolume_ValueChanged(
            object sender, RoutedPropertyChangedEventArgs<double> e)
            => _audio.SetVolume((float)(SldVolume.Value / 100.0));

        private void UpdateMicLabel(string text, bool ok)
        {
            // When ok: hide the top warning label and show device name inside VolumePanel instead.
            LblMicName.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            if (!ok)
            {
                LblMicName.Text = text;
                LblMicName.Foreground = System.Windows.Media.Brushes.Red;
            }

            if (LblSelectedDeviceName != null)
                LblSelectedDeviceName.Text = text;

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

                _audio.AttachDevice(_settings.MicId);

                UpdateMicLabel(_settings.MicName, ok: true);
                SetupVolumeSlider();
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Settings window
        // ══════════════════════════════════════════════════════════════════
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
                _    => "LangNameRu"
            };
            string langName = TryGetResource(langKey, _settings.LanguagePrimary.ToUpper());

            if (LblCurrentLanguage != null)
                LblCurrentLanguage.Text = string.IsNullOrEmpty(activeKey)
                    ? $"🌐 {langName}"
                    : $"🌐 {langName} [{activeKey}]";
        }

        private void BtnLanguageSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow.IsVisible)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow { Owner = this };
            _settingsWindow.Closed += (_, _) =>
            {
                UpdateLanguageButton();
                SetupHotkeys();
            };
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        // ══════════════════════════════════════════════════════════════════
        // VAD animation helpers
        // ══════════════════════════════════════════════════════════════════
        private void StartVadAnimation()
        {
            _vadAnim ??= new DoubleAnimation
            {
                From = 1.0,
                To = 0.1,
                Duration = TimeSpan.FromSeconds(0.75),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            VadDot.BeginAnimation(UIElement.OpacityProperty, _vadAnim);
            VadPanel.Visibility = Visibility.Visible;
        }

        private void StopVadAnimation()
        {
            VadDot.BeginAnimation(UIElement.OpacityProperty, null);
            VadDot.Opacity = 0;
            VadPanel.Visibility = Visibility.Collapsed;
        }

        private void StartRecordingTimer()
        {
            _recSeconds = 0;
            _recTimer?.Stop();
            LblMicName.Visibility = Visibility.Visible;
            _recTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _recTimer.Tick += (_, _) =>
            {
                _recSeconds++;
                int m = _recSeconds / 60;
                int s = _recSeconds % 60;
                LblMicName.Text = $"{(string)FindResource("LblRecording")} {m}:{s:D2}";
            };
            _recTimer.Start();
        }

        private void StopRecordingTimer()
        {
            _recTimer?.Stop();
            _recTimer = null;
            _recSeconds = 0;
        }

        private void ShowProcessingPanel(bool show)
        {
            ProcessingPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) LblStatus.Text = "";
        }

        // ══════════════════════════════════════════════════════════════════
        // Misc button handlers
        // ══════════════════════════════════════════════════════════════════
        private void BtnSound_Click(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = "shell32.dll,Control_RunDLL mmsys.cpl,,1",
                UseShellExecute = true
            });

        private void BtnPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (_promptWindow.IsVisible) _promptWindow.Hide();
            else { _promptWindow.LoadTags(); _promptWindow.Show(); _promptWindow.Activate(); }
        }

        private void BtnOpenNotepad_Click(object sender, RoutedEventArgs e) =>
            ToggleWindow(_notepad);

        // ══════════════════════════════════════════════════════════════════
        // Utilities
        // ══════════════════════════════════════════════════════════════════
        private string LoadDictPrompt()
        {
            try
            {
                if (!File.Exists(DictPath)) return "";
                string raw = File.ReadAllText(DictPath)
                    .Replace("\r\n", " ").Replace("\n", " ").Replace("\"", "");
                return raw.Length > 250 ? raw[..250] : raw;
            }
            catch { return ""; }
        }

        private void ClearLogs()
        {
            try { if (File.Exists(LogPath)) File.Delete(LogPath); } catch { }
        }

        private void CleanupTempFiles()
        {
            try
            {
                string txt = Path.Combine(Path.GetTempPath(), "WhisperVoice_temp.wav.txt");
                if (File.Exists(TempWavPath)) File.Delete(TempWavPath);
                if (File.Exists(txt)) File.Delete(txt);
            }
            catch { }
        }

        private void WriteLog(string text)
        {
            try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} | {text}\n"); }
            catch { }
        }

        // Вспомогательные методы для попапа с ошибкой
        private void ShowErrorPopup(string resourceKey)
        {
            // Запускаем в главном UI-потоке, так как вызов может прийти из фонового потока
            Dispatcher.InvokeAsync(() =>
            {
                string message = TryGetResource(resourceKey, resourceKey);
                string title = TryGetResource("MsgErrorTitle", "Error"); // Fallback

                System.Windows.MessageBox.Show(
                    this, message, title,
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            });
        }

        private void ShowInfoPopup(string message)
        {
            Dispatcher.InvokeAsync(() =>
            {
                string title = TryGetResource("MsgInfoTitle", "Info");

                System.Windows.MessageBox.Show(
                    this, message, title,
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            });
        }

        private string TryGetResource(string key, string fallback)
        {
            try { return (string)FindResource(key); }
            catch { return fallback; }
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