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
    /// <summary>One entry in the transcription history list.</summary>
    public class TranscriptionEntry
    {
        public string Text { get; set; } = "";
        public string TimeLabel { get; set; } = "";

        /// <summary>Up to 120 chars shown in the ListBox row.</summary>
        public string ShortText =>
            Text.Length > 120 ? Text[..117] + "..." : Text;
    }

    /// <summary>A .bin model file discovered in /models.</summary>
    public class ModelEntry
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public override string ToString() => Name;
    }

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

            // VU meter feed from active recorder
            recorder.PeakAvailable += val => Dispatcher.InvokeAsync(() => VuMeter.Value = val);
            recorder.SilenceDetected += () => Dispatcher.InvokeAsync(OnVadSilenceDetected);

            // Bind history list
            HistoryList.ItemsSource = _history;

            LoadModels();
            LoadMicFromSettings();
            SetupHotkeys();

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
        // Hotkeys
        // ══════════════════════════════════════════════════════════════════
        private void SetupHotkeys()
        {
            try
            {
                HotkeyManager.Current.AddOrReplace("ToggleMenu", Key.F7, ModifierKeys.None, OnToggleMenu);
                HotkeyManager.Current.AddOrReplace("RecordRu", Key.F8, ModifierKeys.None, OnRecordRu);
                HotkeyManager.Current.AddOrReplace("RecordEn", Key.F9, ModifierKeys.None, OnRecordEn);
                HotkeyManager.Current.AddOrReplace("Translate", Key.F9, ModifierKeys.Control, OnTranslate);
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
        { e.Handled = true; if (!IsSpam()) ToggleWindow(notepad); }

        private void OnRecordRu(object? s, NHotkey.HotkeyEventArgs e)
        { e.Handled = true; if (!IsSpam() && !isProcessing) ToggleRecording(RecordMode.Ru, "ru", "F8", false); }

        private void OnRecordEn(object? s, NHotkey.HotkeyEventArgs e)
        { e.Handled = true; if (!IsSpam() && !isProcessing) ToggleRecording(RecordMode.En, "en", "F9", false); }

        private void OnTranslate(object? s, NHotkey.HotkeyEventArgs e)
        { e.Handled = true; if (!IsSpam() && !isProcessing) ToggleRecording(RecordMode.Translate, "ru", "Ctrl+F9", true); }

        // ══════════════════════════════════════════════════════════════════
        // Recording toggle (hotkey → UI thread)
        // ══════════════════════════════════════════════════════════════════
        private async void ToggleRecording(RecordMode mode, string lang, string keyName, bool isTranslate)
        {
            if (string.IsNullOrEmpty(_settings.MicId)) { Show(); return; }

            if (!recorder.IsRecording)
            {
                // ── Start recording ──
                activeMode = mode;
                _currentLang = lang;
                _currentTranslate = isTranslate;

                if (File.Exists(tempWavPath)) File.Delete(tempWavPath);
                _silentCapture?.StopRecording();

                // Configure VAD from settings
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

        // ── VAD fires this from the audio thread (dispatched to UI) ──────
        private async void OnVadSilenceDetected()
        {
            if (!recorder.IsRecording || activeMode == RecordMode.None) return;
            WriteLog("VAD: silence threshold reached — auto-stopping.");
            await StopAndProcessAsync();
        }

        // ══════════════════════════════════════════════════════════════════
        // Stop & process  (shared by hotkey + VAD)
        // ══════════════════════════════════════════════════════════════════
        private async Task StopAndProcessAsync()
        {
            // Guard: prevent concurrent calls
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
        // Whisper execution  (Prompt #1 + #3)
        // ══════════════════════════════════════════════════════════════════
        private async Task ProcessWhisperAsync(
            string lang, bool isTranslate,
            IProgress<string> progress, CancellationToken token)
        {
            try
            {
                // 1 ── Resource guard ──────────────────────────────────────
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

                // 2 ── Prepare args ────────────────────────────────────────
                if (File.Exists(tempTxtPath)) File.Delete(tempTxtPath);

                string techPrompt = LoadDictPrompt();
                string model = await Dispatcher.InvokeAsync(
                    () => (ModelCombo.SelectedItem as ModelEntry)?.Path
                          ?? System.IO.Path.Combine(baseDir, "models", "ggml-large-v3.bin"));

                int threads = Math.Max(2, Environment.ProcessorCount - 1);

                string args = BuildWhisperArgs(model, lang, isTranslate, techPrompt, threads);

                WriteLog($"whisper-cli args: {args}");
                progress.Report("🔍 Запуск Whisper...");

                // 3 ── Launch process ──────────────────────────────────────
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
                    // Forward progress hints (Whisper writes % to stderr)
                    if (e.Data.Contains('%') || e.Data.Contains("whisper_")) return;
                    progress.Report(e.Data);
                };

                // Awaitable exit TCS
                var exitTcs = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (s, e) => exitTcs.TrySetResult(true);

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 4 ── Wait with cancellation ──────────────────────────────
                using var cancelReg = token.Register(() =>
                {
                    exitTcs.TrySetCanceled();
                    KillProcessTree(process);
                    WriteLog("Whisper process cancelled by user.");
                });

                try { await exitTcs.Task; }
                catch (OperationCanceledException) { return; }

                token.ThrowIfCancellationRequested();

                // 5 ── Exit code analysis ──────────────────────────────────
                int exitCode = process.ExitCode;
                if (exitCode != 0)
                {
                    string errMsg = exitCode switch
                    {
                        unchecked((int)0xC0000135) =>
                            "Не найдена необходимая DLL (GGML/CUDA). Проверьте наличие dll рядом с exe.",
                        unchecked((int)0xC0000005) =>
                            "Access violation / OOM — возможно VRAM переполнена. Попробуйте меньшую модель.",
                        1 =>
                            "whisper-cli вернул код 1 — нехватка памяти или некорректный WAV/модель.",
                        _ =>
                            $"whisper-cli завершился с кодом {exitCode}."
                    };
                    WriteLog($"whisper exit code {exitCode}: {errMsg}");
                    progress.Report($"❌ Ошибка (код {exitCode})");

                    // ИСПРАВЛЕНИЕ: Теперь всегда показываем ошибку, чтобы программа не вылетала "молча"
                    await Dispatcher.InvokeAsync(() =>
                        System.Windows.MessageBox.Show(this, errMsg, "Ошибка Whisper",
                            MessageBoxButton.OK, MessageBoxImage.Error));

                    return;
                }

                // 6 ── Read & sanity-check result ──────────────────────────
                if (!File.Exists(tempTxtPath))
                {
                    WriteLog("Output file not found after successful exit.");
                    return;
                }

                string result = File.ReadAllText(tempTxtPath).Trim();
                WriteLog($"Raw result ({result.Length} chars): {result[..Math.Min(80, result.Length)]}");

                if (!SanityCheck(result, out string cleanResult))
                {
                    WriteLog($"Hallucination/junk filtered: {result}");
                    progress.Report("⚠️ Результат отфильтрован (галлюцинация)");
                    return;
                }

                // 7 ── Paste & add to history ──────────────────────────────
                progress.Report("✅ Готово!");
                await Dispatcher.InvokeAsync(async () =>
                {
                    AddToHistory(cleanResult);
                    System.Windows.Clipboard.SetText(cleanResult);
                    await Task.Delay(100);
                    inputSim.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.VK_V);
                });
            }
            catch (OperationCanceledException)
            {
                WriteLog("ProcessWhisperAsync cancelled.");
            }
            catch (Exception ex)
            {
                WriteLog($"ProcessWhisperAsync unhandled: {ex}");
                await Dispatcher.InvokeAsync(() =>
                    System.Windows.MessageBox.Show(this,
                        $"Непредвиденная ошибка:\n{ex.Message}", "Ошибка",
                        MessageBoxButton.OK, MessageBoxImage.Error));
            }
        }

        // ── Argument builder ────────────────────────────────────────────
        private static string BuildWhisperArgs(
            string model, string lang, bool isTranslate, string prompt, int threads)
        {
            var sb = new StringBuilder();
            sb.Append($"-m \"{model}\"");
            sb.Append($" -f \"{Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp.wav")}\"");
            sb.Append($" -l {lang}");
            if (isTranslate) sb.Append(" -tr");
            if (!string.IsNullOrWhiteSpace(prompt))
                sb.Append($" --prompt \"{prompt}\"");
            sb.Append(" -otxt -nt -np");
            sb.Append($" -t {threads}");         // use available cores

            // ИСПРАВЛЕНИЕ: Убрали --no-gpu-fallback. Теперь если видеокарта не тянет, оно перейдет на проц, а не вылетит с ошибкой.

            return sb.ToString();
        }

        // ── Kill process + all children ─────────────────────────────────
        private static void KillProcessTree(Process process)
        {
            try
            {
                var kill = new ProcessStartInfo("taskkill",
                    $"/F /T /PID {process.Id}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(kill)?.WaitForExit(3000);
            }
            catch
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Resource checks  (Prompt #1)
        // ══════════════════════════════════════════════════════════════════
        /// <summary>Returns (ok, message). Uses MemoryFailPoint probe.</summary>
        private static Task<(bool ok, string msg)> CheckRamAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    // Probe 400 MB — minimum comfortable headroom for large model
                    using var _ = new MemoryFailPoint(400);
                    return (true, "");
                }
                catch (InsufficientMemoryException)
                {
                    return (false,
                        "Недостаточно свободной оперативной памяти (менее 400 МБ).\n" +
                        "Закройте лишние приложения и попробуйте снова.");
                }
            });
        }

        /// <summary>
        /// Tries to query free VRAM via nvidia-smi.
        /// Returns (true,"") if unavailable (AMD/Intel = no problem) or if free ≥ 1 GB.
        /// </summary>
        private static async Task<(bool ok, string msg)> CheckVramAsync()
        {
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi",
                    "--query-gpu=memory.free --format=csv,noheader,nounits")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };

                using var p = Process.Start(psi);
                if (p == null) return (true, "");

                string raw = await p.StandardOutput.ReadToEndAsync()
                                    .WaitAsync(TimeSpan.FromSeconds(4));

                // nvidia-smi can list multiple GPUs; take the minimum
                long minFree = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => long.TryParse(l.Trim(), out long v) ? v : long.MaxValue)
                    .Min();

                if (minFree < 1000)   // < 1 GB free VRAM
                    return (false,
                        $"VRAM почти заполнена (свободно ≈ {minFree} МБ).\n" +
                        "Это может привести к ошибкам или зависанию.");

                return (true, "");
            }
            catch
            {
                return (true, "");  // nvidia-smi not available — skip silently
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Hallucination filter  (Prompt #1)
        // ══════════════════════════════════════════════════════════════════
        private static readonly string[] _hallucinationPatterns = new string[]
        {
            // Common Whisper hallucinations (Eng)
            "amara.org", "subtitle by", "subtitles by", "subtitled by",
            "transcribed by", "closed captioning", "closed caption",
            "thanks for watching", "thank you for watching",
            "like and subscribe", "please subscribe",
            "dimatorzok", "dima torzok",

            // Common Whisper hallucinations (Ru)
            "спасибо за субтитры", "алексею дубровскому", "продолжение следует",
            "редактор субтитров", "субтитры создавал", "субтитры делал",
            "перевод на русский", "субтитры от", "субтитры добавил",
            "спасибо за просмотр", "дима торзок", "дима торжок",

            // Noise / silence artifacts
            "[ музыка ]", "[ музыка", "[ music ]", "[music]",
            "[ applause ]", "[applause]", "[ silence ]",
            "♪", "www.", ".com", ".org", ".net",
        };

        /// <summary>
        /// Returns true and a cleaned string if the text passes sanity checks.
        /// Returns false if the text is a known hallucination or too short.
        /// </summary>
        private bool SanityCheck(string text, out string cleaned)
        {
            cleaned = "";
            if (string.IsNullOrWhiteSpace(text)) return false;

            // ИСПРАВЛЕНИЕ: Уменьшил порог с 3 до 2, чтобы пропускало слова типа "Да", "Ок"
            int alphaCount = text.Count(char.IsLetterOrDigit);
            if (alphaCount < 2) return false;

            string lower = text.ToLowerInvariant();

            foreach (string pat in _hallucinationPatterns)
                if (lower.Contains(pat)) return false;

            // Optionally strip leading/trailing whitespace & null bytes
            cleaned = text.Trim('\0', '\r', '\n', ' ', '\t');
            return cleaned.Length > 0;
        }

        // ══════════════════════════════════════════════════════════════════
        // Model selector  (Prompt #2)
        // ══════════════════════════════════════════════════════════════════
        private void LoadModels()
        {
            string modelsDir = System.IO.Path.Combine(baseDir, "models");
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

            // Fallback: add default path even if file doesn't exist yet
            if (ModelCombo.Items.Count == 0)
            {
                ModelCombo.Items.Add(new ModelEntry
                {
                    Name = "ggml-large-v3 (по умолчанию)",
                    Path = System.IO.Path.Combine(baseDir, "models", "ggml-large-v3.bin")
                });
            }

            // Restore last selection
            if (!string.IsNullOrEmpty(_settings.LastModelPath))
            {
                var saved = ModelCombo.Items.Cast<ModelEntry>()
                    .FirstOrDefault(m => m.Path == _settings.LastModelPath);
                if (saved != null)
                {
                    ModelCombo.SelectedItem = saved;
                    return;
                }
            }

            ModelCombo.SelectedIndex = 0;
        }

        private void ModelCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ModelCombo.SelectedItem is ModelEntry entry)
            {
                _settings.LastModelPath = entry.Path;
                _settings.Save();
            }
        }

        private void BtnRefreshModels_Click(object sender, RoutedEventArgs e) => LoadModels();

        // ══════════════════════════════════════════════════════════════════
        // Transcription history  (Prompt #2)
        // ══════════════════════════════════════════════════════════════════
        private void AddToHistory(string text)
        {
            _history.Insert(0, new TranscriptionEntry
            {
                Text = text,
                TimeLabel = DateTime.Now.ToString("HH:mm:ss")
            });

            while (_history.Count > MaxHistory)
                _history.RemoveAt(_history.Count - 1);
        }

        private async void HistoryList_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (HistoryList.SelectedItem is not TranscriptionEntry entry) return;

            try
            {
                System.Windows.Clipboard.SetText(entry.Text);
                ShowCopyFeedback();
            }
            catch { }

            await Task.Delay(200);
            HistoryList.SelectedItem = null;
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
            => _history.Clear();

        private async void ShowCopyFeedback()
        {
            CopyFeedback.Visibility = Visibility.Visible;
            await Task.Delay(1200);
            CopyFeedback.Visibility = Visibility.Collapsed;
        }

        // ══════════════════════════════════════════════════════════════════
        // Cancel button  (Prompt #3)
        // ══════════════════════════════════════════════════════════════════
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _whisperCts?.Cancel();
            LblStatus.Text = "⛔ Отменено пользователем";
            WriteLog("User pressed Cancel.");
        }

        // ══════════════════════════════════════════════════════════════════
        // VAD animation helpers  (Prompt #2)
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
            if (_settings.HasMic)
            {
                UpdateMicLabel(_settings.MicName, true);
                SetupVolumeControl();
            }
            else
            {
                UpdateMicLabel("⚠️ ВЫБЕРИТЕ МИКРОФОН!", false);
            }
        }

        private void SetupVolumeControl()
        {
            try
            {
                if (currentDevice != null)
                {
                    currentDevice.AudioEndpointVolume.OnVolumeNotification
                        -= AudioEndpointVolume_OnVolumeNotification;
                    _silentCapture?.StopRecording();
                    _silentCapture?.Dispose();
                }

                var enumerator = new MMDeviceEnumerator();
                currentDevice = enumerator.GetDevice(_settings.MicId);

                _silentCapture = new WasapiCapture(currentDevice, true, 50);
                _silentCapture.DataAvailable += SilentCapture_DataAvailable;
                _silentCapture.StartRecording();

                currentDevice.AudioEndpointVolume.OnVolumeNotification
                    += AudioEndpointVolume_OnVolumeNotification;

                SldVolume.ValueChanged -= SldVolume_ValueChanged;
                SldVolume.Value = currentDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100;
                SldVolume.ValueChanged += SldVolume_ValueChanged;

                VolumePanel.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { WriteLog($"Volume control error: {ex.Message}"); }
        }

        private DateTime _lastSilentPeak = DateTime.MinValue;
        private void SilentCapture_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (recorder.IsRecording) return;   // recorder has its own meter during recording
            var now = DateTime.UtcNow;
            if ((now - _lastSilentPeak).TotalMilliseconds < 40) return;
            _lastSilentPeak = now;

            if (_silentCapture != null)
            {
                double peak = AudioRecorder.CalculatePeak(e.Buffer, e.BytesRecorded,
                                                          _silentCapture.WaveFormat);
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
                currentDevice.AudioEndpointVolume.MasterVolumeLevelScalar =
                    (float)(SldVolume.Value / 100.0);
        }

        private void UpdateMicLabel(string text, bool isOk)
        {
            LblMicName.Text = text;
            LblMicName.Foreground = isOk
                ? System.Windows.Media.Brushes.Blue
                : System.Windows.Media.Brushes.Red;
            VolumePanel.Visibility = isOk ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SyncVolumeFromSystem()
        {
            if (currentDevice == null) return;
            SldVolume.ValueChanged -= SldVolume_ValueChanged;
            SldVolume.Value = currentDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100;
            SldVolume.ValueChanged += SldVolume_ValueChanged;
        }

        // ── Mic selection dialog ──────────────────────────────────────────
        private void BtnSelectMic_Click(object sender, RoutedEventArgs e)
        {
            var mic = new MicWindow { Owner = this };
            if (mic.ShowDialog() == true)
            {
                _settings.MicId = mic.SelectedMicId;
                _settings.MicName = mic.SelectedMicName;
                _settings.Save();
                UpdateMicLabel(_settings.MicName, true);
                SetupVolumeControl();
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Dictionary / prompt helper
        // ══════════════════════════════════════════════════════════════════
        private string LoadDictPrompt()
        {
            try
            {
                if (!File.Exists(dictPath)) return "";
                string raw = File.ReadAllText(dictPath)
                    .Replace("\r\n", " ").Replace("\n", " ").Replace("\"", "");
                return raw.Length > 250 ? raw[..250] : raw;
            }
            catch { return ""; }
        }

        // ══════════════════════════════════════════════════════════════════
        // Misc UI / utility
        // ══════════════════════════════════════════════════════════════════
        private void BtnSound_Click(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = "shell32.dll,Control_RunDLL mmsys.cpl,,1",
                UseShellExecute = true
            });

        private void BtnPrompt_Click(object sender, RoutedEventArgs e)
        { if (promptWindow.IsVisible) promptWindow.Hide(); else { promptWindow.LoadTags(); promptWindow.Show(); promptWindow.Activate(); } }

        private void BtnHelp_Click(object sender, RoutedEventArgs e) =>
            ToggleWindow(helpWindow);

        private void BtnOpenNotepad_Click(object sender, RoutedEventArgs e) =>
            ToggleWindow(notepad);

        // ── Logging ──────────────────────────────────────────────────────
        private void clearLogs() { try { if (File.Exists(logPath)) File.Delete(logPath); } catch { } }
        private void CleanupTempFiles()
        {
            try
            {
                if (File.Exists(tempWavPath)) File.Delete(tempWavPath);
                if (File.Exists(tempTxtPath)) File.Delete(tempTxtPath);
            }
            catch { }
        }

        private void WriteLog(string text)
        {
            try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} | {text}\n"); }
            catch { }
        }
    }
}