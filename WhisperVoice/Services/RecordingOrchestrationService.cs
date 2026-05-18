using System;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WhisperVoice;

namespace WhisperVoice.Services
{
    // ── Event argument types ────────────────────────────────────────────────

    public enum RecordingState { Idle, Recording, Processing }

    public sealed class RecordingStateChangedEventArgs : EventArgs
    {
        public RecordingState State         { get; }
        public AudioSource    Source        { get; }
        public ProcessingMode Mode          { get; }
        public int            TimerSeconds  { get; }

        public RecordingStateChangedEventArgs(
            RecordingState state,
            AudioSource source     = AudioSource.Microphone,
            ProcessingMode mode    = ProcessingMode.Primary,
            int timerSeconds       = 0)
        {
            State        = state;
            Source       = source;
            Mode         = mode;
            TimerSeconds = timerSeconds;
        }
    }

    public sealed class TranscriptionResultEventArgs : EventArgs
    {
        public string Text        { get; }
        public string Lang        { get; }
        public bool   IsTranslate { get; }

        public TranscriptionResultEventArgs(string text, string lang, bool isTranslate)
        {
            Text        = text;
            Lang        = lang;
            IsTranslate = isTranslate;
        }
    }

    // ── Service contract ────────────────────────────────────────────────────

    /// <summary>
    /// Owns the full recording state machine:
    /// start → capture → PCM check → whisper inference → post-process → output.
    /// MainWindow subscribes to events and forwards UI commands only.
    /// </summary>
    public sealed class RecordingOrchestrationService : IDisposable
    {
        // ── Events ──────────────────────────────────────────────────────────
        public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;
        public event EventHandler<TranscriptionResultEventArgs>?   TranscriptionCompleted;
        public event EventHandler<string>?                         StatusUpdated;
        public event EventHandler<int>?                            RecordingTimerTick;
        public event EventHandler?                                 MissingModelRequested;
        public event EventHandler<string>?                         ErrorOccurred;

        // ── Dependencies ────────────────────────────────────────────────────
        private readonly AudioCaptureService    _micCapture;
        private readonly AudioCaptureService    _loopbackCapture;
        private readonly WhisperExecutionService _whisper;
        private readonly HardwareCheckService   _hardware;
        private readonly HallucinationFilter    _hallucinationFilter;
        private readonly TextPostProcessorService _postProcessor;
        private readonly string                 _tempWavPath;

        // ── State ────────────────────────────────────────────────────────────
        private AudioCaptureService _activeCapture;

        private enum InternalMode { None, Primary, Translate, Prompt }
        private InternalMode _activeMode    = InternalMode.None;
        private string       _currentLang   = "ru";
        private bool         _currentTranslate;

        private volatile bool _isProcessing;
        private int           _stopGuard;

        private CancellationTokenSource? _whisperCts;

        // ── Timer ────────────────────────────────────────────────────────────
        private System.Windows.Threading.DispatcherTimer? _recTimer;
        private int _recSeconds;

        public bool IsProcessing => _isProcessing;
        public bool IsRecording  => _activeCapture.IsRecording;
        public AudioCaptureService MicCapture      => _micCapture;
        public AudioCaptureService LoopbackCapture => _loopbackCapture;

        // ── Constructor ──────────────────────────────────────────────────────
        public RecordingOrchestrationService(
            AudioCaptureService    micCapture,
            AudioCaptureService    loopbackCapture,
            WhisperExecutionService whisper,
            HardwareCheckService   hardware,
            HallucinationFilter    hallucinationFilter,
            TextPostProcessorService postProcessor,
            string                 tempWavPath)
        {
            _micCapture          = micCapture;
            _loopbackCapture     = loopbackCapture;
            _whisper             = whisper;
            _hardware            = hardware;
            _hallucinationFilter = hallucinationFilter;
            _postProcessor       = postProcessor;
            _tempWavPath         = tempWavPath;
            _activeCapture       = micCapture;
        }

        // ── Public API ───────────────────────────────────────────────────────

        public void StartRecording(RecordingRequest request)
        {
            var settings = AppSettings.Load();

            if (string.IsNullOrEmpty(settings.MicId) && request.Source == AudioSource.Microphone)
            {
                ErrorOccurred?.Invoke(this, "ErrMicUnplugged");
                return;
            }

            if (string.IsNullOrEmpty(settings.LastModelPath) || !File.Exists(settings.LastModelPath))
            {
                MissingModelRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            _activeMode       = (InternalMode)(int)request.Mode;
            _currentTranslate = request.Mode == ProcessingMode.Translate || request.Mode == ProcessingMode.Prompt;
            _currentLang      = request.Mode == ProcessingMode.Primary ? settings.LanguagePrimary : "en";

            if (File.Exists(_tempWavPath)) File.Delete(_tempWavPath);

            _activeCapture = request.Source == AudioSource.Loopback ? _loopbackCapture : _micCapture;

            double silenceTimeout = request.Source == AudioSource.Loopback
                ? settings.VadSilenceSeconds + 3.0
                : settings.VadSilenceSeconds;

            bool enableVad = request.Source == AudioSource.Loopback && !settings.IsPushToTalkEnabled;

            bool started = _activeCapture.StartRecording(
                settings.MicId, _tempWavPath,
                settings.VadThreshold, silenceTimeout, enableVad);

            if (!started)
            {
                ErrorOccurred?.Invoke(this, "ErrMicUnplugged");
                return;
            }

            if (settings.SoundNotifications) SystemSounds.Beep.Play();

            StartTimer(request.Source == AudioSource.Loopback);

            StateChanged?.Invoke(this, new RecordingStateChangedEventArgs(
                RecordingState.Recording, request.Source, request.Mode));
        }

        public async Task StopAndProcessAsync(AppSettings settings, Func<string, string> getResource)
        {
            if (Interlocked.Exchange(ref _stopGuard, 1) != 0) return;
            _isProcessing = true;
            try
            {
                await _activeCapture.StopRecordingAsync();
                StopTimer();

                StateChanged?.Invoke(this, new RecordingStateChangedEventArgs(RecordingState.Processing));

                if (!IsAudioWorthProcessing(_tempWavPath))
                {
                    _activeMode = InternalMode.None;
                    StateChanged?.Invoke(this, new RecordingStateChangedEventArgs(RecordingState.Idle));
                    return;
                }

                if (settings.SoundNotifications) SystemSounds.Exclamation.Play();

                var mode      = _activeMode;
                var lang      = _currentLang;
                var translate = _currentTranslate;
                _activeMode = InternalMode.None;

                _whisperCts = new CancellationTokenSource();
                var progress = new Progress<string>(msg =>
                {
                    if (!string.IsNullOrWhiteSpace(msg)) StatusUpdated?.Invoke(this, msg);
                });

                string selectedPrompt = mode switch
                {
                    InternalMode.Translate => settings.PromptTranslate,
                    InternalMode.Prompt    => LoadDictPrompt(settings),
                    _                      => string.Empty
                };

                await RunWhisperPipelineAsync(
                    lang, translate, selectedPrompt,
                    progress, settings, getResource,
                    _whisperCts.Token);
            }
            finally
            {
                _isProcessing = false;
                Interlocked.Exchange(ref _stopGuard, 0);
                _activeCapture = _micCapture;
                StateChanged?.Invoke(this, new RecordingStateChangedEventArgs(RecordingState.Idle));
            }
        }

        public void CancelWhisper()
        {
            _whisperCts?.Cancel();
        }

        // ── Whisper pipeline ─────────────────────────────────────────────────

        private async Task RunWhisperPipelineAsync(
            string lang, bool isTranslate, string techPrompt,
            IProgress<string> progress, AppSettings settings,
            Func<string, string> getResource,
            CancellationToken token)
        {
            try
            {
                string ramFmt  = getResource("ErrLowRam");
                string vramFmt = getResource("ErrLowVram");

                var (ramOk, ramMsg) = await _hardware.CheckRamAsync(ramFmt);
                if (!ramOk)
                {
                    ErrorOccurred?.Invoke(this, ramMsg);
                    return;
                }

                var (vramOk, vramMsg) = await _hardware.CheckVramAsync(vramFmt);
                if (!vramOk)
                {
                    string combined = vramMsg + getResource("MsgVramContinue");
                    ErrorOccurred?.Invoke(this, combined);
                    return;
                }

                string model = AppSettings.Load().LastModelPath;
                if (string.IsNullOrEmpty(model) || !File.Exists(model))
                {
                    DiagnosticLogger.Instance.Error("RecordingOrchestrationService",
                        "Model file missing before inference: " + model);
                    MissingModelRequested?.Invoke(this, EventArgs.Empty);
                    return;
                }

                string? rawResult = await _whisper.RunAsync(
                    model, lang, isTranslate, techPrompt,
                    progress,
                    msg => DiagnosticLogger.Instance.Info("RecordingOrchestrationService", msg),
                    token,
                    beamSize:           settings.BeamSize,
                    bestOf:             settings.BestOf,
                    temperature:        settings.Temperature,
                    noSpeechThreshold:  settings.NoSpeechThreshold);

                if (rawResult is null) return;

                if (!_hallucinationFilter.Check(rawResult, out string cleanResult))
                {
                    progress.Report(getResource("MsgHallucinationFiltered"));
                    return;
                }

                string finalResult = _postProcessor.Process(cleanResult);

                if (string.IsNullOrWhiteSpace(finalResult) || !finalResult.Any(char.IsLetterOrDigit))
                {
                    progress.Report("Silence / Ignored");
                    return;
                }

                progress.Report(getResource("MsgWhisperDone"));

                TranscriptionCompleted?.Invoke(this,
                    new TranscriptionResultEventArgs(finalResult, lang, isTranslate));
            }
            catch (OperationCanceledException) { /* user cancelled — no-op */ }
            catch (Exception ex)
            {
                DiagnosticLogger.Instance.Error("RecordingOrchestrationService", ex, "Pipeline failed");
                ErrorOccurred?.Invoke(this, ex.Message);
            }
        }

        // ── PCM quality gate ─────────────────────────────────────────────────

        private bool IsAudioWorthProcessing(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= 44) return false;

                byte[]? bytes = null;
                for (int i = 0; i < 3; i++)
                {
                    try { bytes = File.ReadAllBytes(path); break; }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Instance.Trace(
                            "RecordingOrchestrationService", $"Read WAV retry: {ex.Message}");
                        Thread.Sleep(50);
                    }
                }
                if (bytes == null || bytes.Length <= 44) return true;

                int sampleCount = (bytes.Length - 44) / 2;
                if (sampleCount < 6400) return false;

                int startSample = 4800;
                int endSample   = sampleCount - 4800;

                if (startSample >= endSample)
                {
                    startSample = sampleCount / 4;
                    endSample   = sampleCount - (sampleCount / 4);
                }

                int validCount = endSample - startSample;
                if (validCount <= 0) return false;

                long sum = 0;
                for (int i = startSample; i < endSample; i++)
                    sum += BitConverter.ToInt16(bytes, 44 + i * 2);
                short dcOffset = (short)(sum / validCount);

                short maxAc = 0;
                for (int i = startSample; i < endSample; i++)
                {
                    short ac = (short)Math.Abs(BitConverter.ToInt16(bytes, 44 + i * 2) - dcOffset);
                    if (ac > maxAc) maxAc = ac;
                }

                DiagnosticLogger.Instance.Info(
                    "RecordingOrchestrationService",
                    $"[PCM Filter] DC Offset: {dcOffset} | True AC Peak: {maxAc}");

                return _activeCapture == _loopbackCapture ? maxAc > 10 : maxAc > 500;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Instance.Warn(
                    "RecordingOrchestrationService",
                    $"[PCM Filter] File error: {ex.Message} -> Fallback to True");
                return true;
            }
        }

        // ── Timer ────────────────────────────────────────────────────────────

        private void StartTimer(bool isLoopback)
        {
            _recSeconds = 0;
            _recTimer?.Stop();

            _recTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _recTimer.Tick += (_, _) =>
            {
                _recSeconds++;
                RecordingTimerTick?.Invoke(this, _recSeconds);
            };
            _recTimer.Start();

            RecordingTimerTick?.Invoke(this, 0);
        }

        private void StopTimer()
        {
            _recTimer?.Stop();
            _recTimer    = null;
            _recSeconds  = 0;
        }

        // ── Dictionary prompt loader ─────────────────────────────────────────

        private static string LoadDictPrompt(AppSettings settings)
        {
            try
            {
                string dictDir  = Path.Combine(AppSettings.AppDataDir, "dictionary");
                string dictPath = Path.Combine(dictDir, "dictionary.txt");
                if (!File.Exists(dictPath)) return string.Empty;
                string raw = File.ReadAllText(dictPath)
                    .Replace("\r\n", " ").Replace("\n", " ").Replace("\"", "");
                return raw.Length > 250 ? raw[..250] : raw;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Instance.Error(
                    "RecordingOrchestrationService", ex, "LoadDictPrompt failed");
                return string.Empty;
            }
        }

        // ── IDisposable ──────────────────────────────────────────────────────

        public void Dispose()
        {
            _whisperCts?.Cancel();
            _whisperCts?.Dispose();
            StopTimer();
        }
    }

    // ── Request DTO ─────────────────────────────────────────────────────────

    public sealed class RecordingRequest
    {
        public ProcessingMode Mode   { get; }
        public AudioSource    Source { get; }

        public RecordingRequest(ProcessingMode mode, AudioSource source)
        {
            Mode   = mode;
            Source = source;
        }
    }
}