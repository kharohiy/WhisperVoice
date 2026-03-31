using NAudio.CoreAudioApi;
using NAudio.Wave;
using NHotkey.Wpf;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using WindowsInput;
using WindowsInput.Native;

namespace WhisperVoice
{
    // ── Data models ────────────────────────────────────────────────────────
    public class TranscriptionEntry
    {
        public string Text { get; set; } = "";
        public string TimeLabel { get; set; } = "";

        public string ShortText =>
            Text.Length > 120 ? Text[..117] + "..." : Text;
    }

    // NOTE: ModelEntry class has been moved to SettingsWindow.xaml.cs

    // ── MainWindow ─────────────────────────────────────────────────────────
    public partial class MainWindow : Window
    {
        // ── Settings & paths ──────────────────────────────────────────────
        private AppSettings _settings = AppSettings.Load();

        private string baseDir => AppDomain.CurrentDomain.BaseDirectory;
        private string tempWavPath => System.IO.Path.Combine(baseDir, "temp.wav");
        private string tempTxtPath => System.IO.Path.Combine(baseDir, "temp.wav.txt");
        private string logPath => System.IO.Path.Combine(baseDir, "whisper_debug.log");
        private string dictPath => System.IO.Path.Combine(baseDir, "dictionary", "dictionary.txt");
        private string whisperExe => System.IO.Path.Combine(baseDir, "whisper-cli.exe");

        // ── NAudio / device ───────────────────────────────────────────────
        private AudioRecorder recorder = new AudioRecorder();
        private WasapiCapture? _silentCapture;
        private MMDevice? currentDevice;

        // ── UI helpers ────────────────────────────────────────────────────
        private System.Windows.Forms.NotifyIcon trayIcon = null!;
        private InputSimulator inputSim = new InputSimulator();

        private NotepadWindow notepad = new NotepadWindow();
        private HelpWindow helpWindow = new HelpWindow();
        private PromptWindow promptWindow = new PromptWindow();
        private SettingsWindow settingsWindow = new SettingsWindow();

        // ── Recording state ───────────────────────────────────────────────
        private enum RecordMode { None, Ru, En, Translate }
        private RecordMode activeMode = RecordMode.None;

        private string _currentLang = "ru";
        private bool _currentTranslate = false;
        private bool isProcessing = false;
        private int _stopGuard = 0;   // Interlocked flag against double-stop

        // ── Async / cancellation ──────────────────────────────────────────
        private CancellationTokenSource? _whisperCts;

        // ── History ───────────────────────────────────────────────────────
        private const int MaxHistory = 10;
        private ObservableCollection<TranscriptionEntry> _history = new();

        // ── VAD animation ─────────────────────────────────────────────────
        private DoubleAnimation? _vadAnim;

        // ── Anti-spam ────────────────────────────────────────────────────
        private DateTime _lastAction = DateTime.MinValue;

        // ══════════════════════════════════════════════════════════════════
        // Constructor
        // ══════════════════════════════════════════════════════════════════
        public MainWindow()
        {
            InitializeComponent();
            clearLogs();
            CleanupTempFiles();
            SetupTrayIcon();

            recorder.PeakAvailable += val => Dispatcher.InvokeAsync(() => VuMeter.Value = val);
            recorder.SilenceDetected += () => Dispatcher.InvokeAsync(OnVadSilenceDetected);

            HistoryList.ItemsSource = _history;

            LoadMicFromSettings();
            SetupHotkeys();

            // Обновляем текст кнопки при запуске программы
            UpdateLanguageButton();

            this.IsVisibleChanged += (s, e) =>
            {
                if (this.IsVisible) SyncVolumeFromSystem();
            };

            System.Windows.Application.Current.Exit += (s, e) => FullShutdown();
        }

        // ══════════════════════════════════════════════════════════════════
        // Tray icon
        // ══════════════════════════════════════════════════════════════════
        private void SetupTrayIcon()
        {
            trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Information,
                Text = "Whisper Voice",
                Visible = true
            };
            trayIcon.DoubleClick += (s, e) => { Show(); Activate(); };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("⚙️ Панель управления (F7)", null, (s, e) => { Show(); Activate(); });
            menu.Items.Add("📝 Блокнот (Ctrl+F7)", null, (s, e) => ToggleWindow(notepad));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("❌ Выход", null, (s, e) =>
            {
                FullShutdown();
                trayIcon.Visible = false;
                trayIcon.Dispose();
                System.Windows.Application.Current.Shutdown();
            });
            trayIcon.ContextMenuStrip = menu;
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
                recorder.StopRecording();
                _silentCapture?.StopRecording();
                _silentCapture?.Dispose();
                CleanupTempFiles();
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════
        // Hotkeys  — reads HotkeyRu / HotkeyEn dynamically from settings
        // ══════════════════════════════════════════════════════════════════
        private void SetupHotkeys()
        {
            try
            {
                _settings = AppSettings.Load();

                var keyRu = (Key)Enum.Parse(typeof(Key), _settings.HotkeyRu, ignoreCase: true);
                var keyEn = (Key)Enum.Parse(typeof(Key), _settings.HotkeyEn, ignoreCase: true);

                HotkeyManager.Current.AddOrReplace("ToggleMenu", Key.F7,  ModifierKeys.None,    OnToggleMenu);
                HotkeyManager.Current.AddOrReplace("RecordRu",   keyRu,   ModifierKeys.None,    OnRecordRu);
                HotkeyManager.Current.AddOrReplace("RecordEn",   keyEn,   ModifierKeys.None,    OnRecordEn);
                HotkeyManager.Current.AddOrReplace("Translate",  Key.F9,  ModifierKeys.Control, OnTranslate);
                HotkeyManager.Current.AddOrReplace("Notepad",    Key.F7,  ModifierKeys.Control, OnOpenNotepad);
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
        { e.Handled = true; if (!IsSpam()) ToggleWindow(notepad); }

        // ЗДЕСЬ ЯЗЫК БЕРЕТСЯ ИЗ НАСТРОЕК ДЛЯ F8
        private void OnRecordRu(object? s, NHotkey.HotkeyEventArgs e)
        {
            e.Handled = true;
            if (!IsSpam() && !isProcessing)
            {
                _settings = AppSettings.Load();
                ToggleRecording(RecordMode.Ru, _settings.LanguageF8, _settings.HotkeyRu, false);
            }
        }

        private void OnRecordEn(object? s, NHotkey.HotkeyEventArgs e)
        { e.Handled = true; if (!IsSpam() && !isProcessing) ToggleRecording(RecordMode.En, "en", _settings.HotkeyEn, false); }

        private void OnTranslate(object? s, NHotkey.HotkeyEventArgs e)
        { e.Handled = true; if (!IsSpam() && !isProcessing) ToggleRecording(RecordMode.Translate, "ru", "Ctrl+F9", true); }

        // ══════════════════════════════════════════════════════════════════
        // Recording toggle
        // ══════════════════════════════════════════════════════════════════
        private async void ToggleRecording(RecordMode mode, string lang, string keyName, bool isTranslate)
        {
            if (string.IsNullOrEmpty(_settings.MicId)) { Show(); return; }

            if (!recorder.IsRecording)
            {
                activeMode = mode;
                _currentLang = lang;
                _currentTranslate = isTranslate;

                if (File.Exists(tempWavPath)) File.Delete(tempWavPath);
                _silentCapture?.StopRecording();

                recorder.VadEnabled = true;
                recorder.VadThreshold = _settings.VadThreshold;
                recorder.VadSilenceTimeout = TimeSpan.FromSeconds(_settings.VadSilenceSeconds);

                recorder.StartRecording(_settings.MicId, tempWavPath);
                StartVadAnimation();

                LblMicName.Text = $"🔴 ЗАПИСЬ...\n(VAD авто-стоп, или {keyName})";
                LblMicName.Foreground = System.Windows.Media.Brushes.Red;
            }
            else
            {
                if (activeMode != mode) return;
                await StopAndProcessAsync();
            }
        }

        private async void OnVadSilenceDetected()
        {
            if (!recorder.IsRecording || activeMode == RecordMode.None) return;
            WriteLog("VAD: silence threshold reached — auto-stopping.");
            await StopAndProcessAsync();
        }

        private async Task StopAndProcessAsync()
        {
            if (Interlocked.Exchange(ref _stopGuard, 1) != 0) return;
            try
            {
                recorder.VadEnabled = false;
                await recorder.StopRecordingAsync();

                StopVadAnimation();

                var lang = _currentLang;
                var translate = _currentTranslate;
                activeMode = RecordMode.None;
                isProcessing = true;

                LblMicName.Text = "🧠 ОБРАБОТКА...\n(Подождите)";
                LblMicName.Foreground = System.Windows.Media.Brushes.Orange;
                VuMeter.Value = 0;
                ShowProcessingPanel(true);

                _whisperCts = new CancellationTokenSource();
                var progress = new Progress<string>(msg =>
                {
                    if (!string.IsNullOrWhiteSpace(msg))
                        LblStatus.Text = msg.Length > 90 ? msg[..87] + "..." : msg;
                });

                await ProcessWhisperAsync(lang, translate, progress, _whisperCts.Token);

                isProcessing = false;
                ShowProcessingPanel(false);
                UpdateMicLabel(_settings.MicName, true);

                try { _silentCapture?.StartRecording(); } catch { }
            }
            finally
            {
                Interlocked.Exchange(ref _stopGuard, 0);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Whisper execution
        // ══════════════════════════════════════════════════════════════════
        private async Task ProcessWhisperAsync(
            string lang, bool isTranslate,
            IProgress<string> progress, CancellationToken token)
        {
            try
            {
                var (ramOk, ramMsg) = await CheckRamAsync();
                if (!ramOk)
                {
                    await Dispatcher.InvokeAsync(() =>
                        System.Windows.MessageBox.Show(this, ramMsg,
                            "⚠️ Недостаточно ресурсов",
                            MessageBoxButton.OK, MessageBoxImage.Warning));
                    return;
                }

                var (vramOk, vramMsg) = await CheckVramAsync();
                if (!vramOk)
                {
                    var choice = await Dispatcher.InvokeAsync(() =>
                        System.Windows.MessageBox.Show(this,
                            vramMsg + "\n\nПродолжить всё равно?",
                            "⚠️ VRAM",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning));
                    if (choice == MessageBoxResult.No) return;
                }

                if (File.Exists(tempTxtPath)) File.Delete(tempTxtPath);

                string techPrompt = LoadDictPrompt();

                // ── Model path read from AppSettings (ModelCombo lives in SettingsWindow) ──
                string model = await Dispatcher.InvokeAsync(() =>
                {
                    string saved = AppSettings.Load().LastModelPath;
                    return !string.IsNullOrEmpty(saved)
                        ? saved
                        : System.IO.Path.Combine(baseDir, "models", "ggml-large-v3.bin");
                });

                int threads = Math.Max(2, Environment.ProcessorCount - 1);

                string args = BuildWhisperArgs(model, lang, isTranslate, techPrompt, threads);

                WriteLog($"whisper-cli args: {args}");
                progress.Report("🔍 Запуск Whisper...");

                var psi = new ProcessStartInfo
                {
                    FileName = whisperExe,
                    Arguments = args,
                    WorkingDirectory = baseDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = new Process
                {
                    StartInfo = psi,
                    EnableRaisingEvents = true
                };

                var outputLines = new List<string>();
                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data is null) return;
                    outputLines.Add(e.Data);
                    progress.Report(e.Data);
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data is null) return;
                    WriteLog($"[whisper stderr] {e.Data}");
                    if (e.Data.Contains('%') || e.Data.Contains("whisper_")) return;
                    progress.Report(e.Data);
                };

                var exitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (s, e) => exitTcs.TrySetResult(true);

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var cancelReg = token.Register(() =>
                {
                    exitTcs.TrySetCanceled();
                    KillProcessTree(process);
                    WriteLog("Whisper process cancelled by user.");
                });

                try { await exitTcs.Task; }
                catch (OperationCanceledException) { return; }

                token.ThrowIfCancellationRequested();

                int exitCode = process.ExitCode;
                if (exitCode != 0)
                {
                    string errMsg = exitCode switch
                    {
                        unchecked((int)0xC0000135) => "Не найдена необходимая DLL (GGML/CUDA).",
                        unchecked((int)0xC0000005) => "Access violation / OOM — возможно VRAM переполнена.",
                        1 => "whisper-cli вернул код 1 — нехватка памяти или некорректный WAV.",
                        _ => $"whisper-cli завершился с кодом {exitCode}."
                    };
                    WriteLog($"whisper exit code {exitCode}: {errMsg}");
                    progress.Report($"❌ Ошибка (код {exitCode})");

                    await Dispatcher.InvokeAsync(() =>
                        System.Windows.MessageBox.Show(this, errMsg, "Ошибка Whisper",
                            MessageBoxButton.OK, MessageBoxImage.Error));
                    return;
                }

                if (!File.Exists(tempTxtPath))
                {
                    WriteLog("Output file not found after successful exit.");
                    return;
                }

                string result = File.ReadAllText(tempTxtPath).Trim();
                WriteLog($"Raw result: {result}");

                if (!SanityCheck(result, out string cleanResult))
                {
                    WriteLog($"Hallucination filtered: {result}");
                    progress.Report("⚠️ Результат отфильтрован");
                    return;
                }

                progress.Report("✅ Готово!");
                await Dispatcher.InvokeAsync(async () =>
                {
                    AddToHistory(cleanResult);
                    System.Windows.Clipboard.SetText(cleanResult);
                    await Task.Delay(100);
                    inputSim.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.VK_V);
                });
            }
            catch (OperationCanceledException) { WriteLog("ProcessWhisperAsync cancelled."); }
            catch (Exception ex)
            {
                WriteLog($"ProcessWhisperAsync unhandled: {ex}");
                await Dispatcher.InvokeAsync(() =>
                    System.Windows.MessageBox.Show(this, $"Ошибка:\n{ex.Message}", "Ошибка",
                        MessageBoxButton.OK, MessageBoxImage.Error));
            }
        }

        private string BuildWhisperArgs(string model, string lang, bool isTranslate, string prompt, int threads)
        {
            var sb = new StringBuilder();
            sb.Append($"-m \"{model}\"");
            sb.Append($" -f \"{tempWavPath}\"");
            sb.Append($" -l {lang}");
            if (isTranslate) sb.Append(" -tr");
            if (!string.IsNullOrWhiteSpace(prompt))
                sb.Append($" --prompt \"{prompt}\"");
            sb.Append(" -otxt -nt -np");
            sb.Append($" -t {threads}");
            return sb.ToString();
        }

        private static void KillProcessTree(Process process)
        {
            try
            {
                var kill = new ProcessStartInfo("taskkill", $"/F /T /PID {process.Id}")
                { UseShellExecute = false, CreateNoWindow = true };
                Process.Start(kill)?.WaitForExit(3000);
            }
            catch { try { process.Kill(entireProcessTree: true); } catch { } }
        }

        // ══════════════════════════════════════════════════════════════════
        // Resource checks
        // ══════════════════════════════════════════════════════════════════
        private static Task<(bool ok, string msg)> CheckRamAsync()
        {
            return Task.Run(() =>
            {
                try { using var _ = new MemoryFailPoint(400); return (true, ""); }
                catch (InsufficientMemoryException) { return (false, "Недостаточно RAM."); }
            });
        }

        private static async Task<(bool ok, string msg)> CheckVramAsync()
        {
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.free --format=csv,noheader,nounits")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };

                using var p = Process.Start(psi);
                if (p == null) return (true, "");

                string raw = await p.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(4));
                long minFree = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => long.TryParse(l.Trim(), out long v) ? v : long.MaxValue).Min();

                if (minFree < 1000) return (false, $"VRAM почти заполнена ({minFree} МБ).");
                return (true, "");
            }
            catch { return (true, ""); }
        }

        // ══════════════════════════════════════════════════════════════════
        // Hallucination filter
        // ══════════════════════════════════════════════════════════════════
        private static readonly string[] _hallucinationPatterns = new string[]
        {
            "amara.org", "subtitle by", "subtitles by", "subtitled by",
            "transcribed by", "closed captioning", "closed caption",
            "thanks for watching", "thank you for watching",
            "like and subscribe", "please subscribe",
            "dimatorzok", "dima torzok",
            "спасибо за субтитры", "алексею дубровскому", "продолжение следует",
            "редактор субтитров", "субтитры создавал", "субтитры делал",
            "перевод на русский", "субтитры от", "субтитры добавил",
            "спасибо за просмотр", "дима торзок", "дима торжок",
            "[ музыка ]", "[ музыка", "[ music ]", "[music]",
            "[ applause ]", "[applause]", "[ silence ]",
            "♪", "www.", ".com", ".org", ".net"
        };

        private bool SanityCheck(string text, out string cleaned)
        {
            cleaned = "";
            if (string.IsNullOrWhiteSpace(text)) return false;

            int alphaCount = text.Count(char.IsLetterOrDigit);
            if (alphaCount < 2) return false;

            string lower = text.ToLowerInvariant();
            foreach (string pat in _hallucinationPatterns)
                if (lower.Contains(pat)) return false;

            cleaned = text.Trim('\0', '\r', '\n', ' ', '\t');
            return cleaned.Length > 0;
        }

        // ══════════════════════════════════════════════════════════════════
        // History
        // ══════════════════════════════════════════════════════════════════
        private void AddToHistory(string text)
        {
            _history.Insert(0, new TranscriptionEntry { Text = text, TimeLabel = DateTime.Now.ToString("HH:mm:ss") });
            while (_history.Count > MaxHistory) _history.RemoveAt(_history.Count - 1);
        }

        private async void HistoryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (HistoryList.SelectedItem is not TranscriptionEntry entry) return;
            try { System.Windows.Clipboard.SetText(entry.Text); ShowCopyFeedback(); } catch { }
            await Task.Delay(200);
            HistoryList.SelectedItem = null;
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e) => _history.Clear();

        private async void ShowCopyFeedback()
        {
            CopyFeedback.Visibility = Visibility.Visible;
            await Task.Delay(1200);
            CopyFeedback.Visibility = Visibility.Collapsed;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _whisperCts?.Cancel();
            LblStatus.Text = "⛔ Отменено";
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

        private void ShowProcessingPanel(bool show)
        {
            ProcessingPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) LblStatus.Text = "";
        }

        // ══════════════════════════════════════════════════════════════════
        // Settings / mic / volume
        // ══════════════════════════════════════════════════════════════════
        private void LoadMicFromSettings()
        {
            if (_settings.HasMic) { UpdateMicLabel(_settings.MicName, true); SetupVolumeControl(); }
            else UpdateMicLabel("⚠️ ВЫБЕРИТЕ МИКРОФОН!", false);
        }

        private void SetupVolumeControl()
        {
            try
            {
                if (currentDevice != null)
                {
                    currentDevice.AudioEndpointVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification;
                    _silentCapture?.StopRecording(); _silentCapture?.Dispose();
                }
                var enumerator = new MMDeviceEnumerator();
                currentDevice = enumerator.GetDevice(_settings.MicId);
                _silentCapture = new WasapiCapture(currentDevice, true, 50);
                _silentCapture.DataAvailable += SilentCapture_DataAvailable;
                _silentCapture.StartRecording();
                currentDevice.AudioEndpointVolume.OnVolumeNotification += AudioEndpointVolume_OnVolumeNotification;
                SldVolume.ValueChanged -= SldVolume_ValueChanged;
                SldVolume.Value = currentDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100;
                SldVolume.ValueChanged += SldVolume_ValueChanged;
                VolumePanel.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { WriteLog($"Volume error: {ex.Message}"); }
        }

        private DateTime _lastSilentPeak = DateTime.MinValue;
        private void SilentCapture_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (recorder.IsRecording) return;
            var now = DateTime.UtcNow;
            if ((now - _lastSilentPeak).TotalMilliseconds < 40) return;
            _lastSilentPeak = now;
            if (_silentCapture != null)
            {
                double peak = AudioRecorder.CalculatePeak(e.Buffer, e.BytesRecorded, _silentCapture.WaveFormat);
                Dispatcher.InvokeAsync(() => VuMeter.Value = peak);
            }
        }

        private void AudioEndpointVolume_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            Dispatcher.Invoke(() =>
            {
                SldVolume.ValueChanged -= SldVolume_ValueChanged;
                SldVolume.Value = data.MasterVolume * 100;
                SldVolume.ValueChanged += SldVolume_ValueChanged;
            });
        }

        private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (currentDevice != null)
                currentDevice.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(SldVolume.Value / 100.0);
        }

        private void UpdateMicLabel(string text, bool isOk)
        {
            LblMicName.Text = text;
            LblMicName.Foreground = isOk ? System.Windows.Media.Brushes.Blue : System.Windows.Media.Brushes.Red;
            VolumePanel.Visibility = isOk ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SyncVolumeFromSystem()
        {
            if (currentDevice == null) return;
            SldVolume.ValueChanged -= SldVolume_ValueChanged;
            SldVolume.Value = currentDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100;
            SldVolume.ValueChanged += SldVolume_ValueChanged;
        }

        private void BtnSelectMic_Click(object sender, RoutedEventArgs e)
        {
            var mic = new MicWindow { Owner = this };
            if (mic.ShowDialog() == true)
            {
                _settings.MicId = mic.SelectedMicId; _settings.MicName = mic.SelectedMicName;
                _settings.Save(); UpdateMicLabel(_settings.MicName, true); SetupVolumeControl();
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Settings Window — open, close, refresh hotkeys & label
        // ══════════════════════════════════════════════════════════════════

        // Updates the button text in MainWindow to show the current F8 language
        private void UpdateLanguageButton()
        {
            _settings = AppSettings.Load();
            string langName = _settings.LanguageF8 switch
            {
                "en" => "English",
                "uk" => "Українська",
                "pl" => "Polski",
                "de" => "Deutsch",
                "es" => "Español",
                "fr" => "Français",
                _    => "Русский"
            };

            if (BtnLanguageSettings != null)
                BtnLanguageSettings.Content = $"⚙️ Язык для F8 ({langName})";
        }

        private void BtnLanguageSettings_Click(object sender, RoutedEventArgs e)
        {
            if (settingsWindow.IsVisible)
            {
                settingsWindow.Activate();
            }
            else
            {
                settingsWindow = new SettingsWindow { Owner = this };

                // On close: refresh language button label AND re-register hotkeys
                settingsWindow.Closed += (s, args) =>
                {
                    UpdateLanguageButton();
                    SetupHotkeys();
                };

                settingsWindow.Show();
                settingsWindow.Activate();
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Misc
        // ══════════════════════════════════════════════════════════════════
        private string LoadDictPrompt()
        {
            try
            {
                if (!File.Exists(dictPath)) return "";
                string raw = File.ReadAllText(dictPath).Replace("\r\n", " ").Replace("\n", " ").Replace("\"", "");
                return raw.Length > 250 ? raw[..250] : raw;
            }
            catch { return ""; }
        }

        private void BtnSound_Click(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo { FileName = "rundll32.exe", Arguments = "shell32.dll,Control_RunDLL mmsys.cpl,,1", UseShellExecute = true });

        private void BtnPrompt_Click(object sender, RoutedEventArgs e)
        { if (promptWindow.IsVisible) promptWindow.Hide(); else { promptWindow.LoadTags(); promptWindow.Show(); promptWindow.Activate(); } }

        private void BtnHelp_Click(object sender, RoutedEventArgs e) => ToggleWindow(helpWindow);

        private void BtnOpenNotepad_Click(object sender, RoutedEventArgs e) => ToggleWindow(notepad);

        private void clearLogs() { try { if (File.Exists(logPath)) File.Delete(logPath); } catch { } }
        private void CleanupTempFiles() { try { if (File.Exists(tempWavPath)) File.Delete(tempWavPath); if (File.Exists(tempTxtPath)) File.Delete(tempTxtPath); } catch { } }
        private void WriteLog(string text) { try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} | {text}\n"); } catch { } }
    }
}
