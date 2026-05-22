using System;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace WhisperVoice.Services
{
    public enum RecordingState { Idle, Recording, Processing }

    public sealed class RecordingStateChangedEventArgs : EventArgs
    {
        public RecordingState State         { get; }
        public AudioSource    Source        { get; }
        public ProcessingMode Mode          { get; }
        public int            TimerSeconds  { get; }

        public RecordingStateChangedEventArgs(RecordingState state, AudioSource source = AudioSource.Microphone, ProcessingMode mode = ProcessingMode.Primary, int timerSeconds = 0)
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

    public sealed class RecordingOrchestrationService : IDisposable
    {
        public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;
        public event EventHandler<TranscriptionResultEventArgs>?   TranscriptionCompleted;
        [Obsolete("Use StatusReported event instead.")]
        public event EventHandler<string>?                         StatusUpdated;
        public event EventHandler<PipelineStatusReport>?           StatusReported;
        public event EventHandler<int>?                            RecordingTimerTick;
        public event EventHandler?                                 MissingModelRequested;
        public event EventHandler<string>?                         ErrorOccurred;
        public event EventHandler<VulkanStatus>?                    VulkanStatusChecked;

        private readonly IAudioCaptureService    _micCapture;
        private readonly IAudioCaptureService    _loopbackCapture;
        private readonly IWhisperExecutionService _whisper;
        private readonly HardwareCheckService   _hardware;
        private readonly HallucinationFilter    _hallucinationFilter;
        private readonly TextPostProcessorService _postProcessor;
        private readonly string                 _tempWavPath;

        private IAudioCaptureService _activeCapture;
        private enum InternalMode { None, Primary, Translate, Prompt }
        
        private readonly object _stateLock = new();
        private PipelineLifecycleState _state = PipelineLifecycleState.Idle;
        private InternalMode _activeMode = InternalMode.None;

        private string       _currentLang   = "ru";
        private bool         _currentTranslate;
        private CancellationTokenSource? _whisperCts;
        private System.Windows.Threading.DispatcherTimer? _recTimer;
        private int _recSeconds;

        public PipelineLifecycleState CurrentState
        {
            get
            {
                lock (_stateLock)
                {
                    return _state;
                }
            }
        }

        public bool IsProcessing => CurrentState != PipelineLifecycleState.Idle && CurrentState != PipelineLifecycleState.Recording;
        public bool IsRecording  => CurrentState == PipelineLifecycleState.Recording;
        public IAudioCaptureService MicCapture      => _micCapture;
        public IAudioCaptureService LoopbackCapture => _loopbackCapture;

        public ProcessingMode ActiveMode { get; private set; } = ProcessingMode.Primary;
        public AudioSource ActiveSource { get; private set; } = AudioSource.Microphone;

        public RecordingOrchestrationService(IAudioCaptureService micCapture, IAudioCaptureService loopbackCapture, IWhisperExecutionService whisper, HardwareCheckService hardware, HallucinationFilter hallucinationFilter, TextPostProcessorService postProcessor, string tempWavPath)
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

        private void TransitionTo(PipelineLifecycleState newState, double progressPercentage = -1.0, string message = "", DiagnosticLogger.Level logLevel = DiagnosticLogger.Level.INFO, PipelineError error = PipelineError.None)
        {
            lock (_stateLock)
            {
                _state = newState;
            }
            switch (logLevel)
            {
                case DiagnosticLogger.Level.TRACE:
                    DiagnosticLogger.Instance.Trace("RecordingOrchestrationService", $"Transitioned to {newState}. Msg: {message}");
                    break;
                case DiagnosticLogger.Level.INFO:
                    DiagnosticLogger.Instance.Info("RecordingOrchestrationService", $"Transitioned to {newState}. Msg: {message}");
                    break;
                case DiagnosticLogger.Level.WARN:
                    DiagnosticLogger.Instance.Warn("RecordingOrchestrationService", $"Transitioned to {newState}. Msg: {message}");
                    break;
                case DiagnosticLogger.Level.ERROR:
                    DiagnosticLogger.Instance.Error("RecordingOrchestrationService", $"Transitioned to {newState}. Msg: {message}");
                    break;
            }
            StatusReported?.Invoke(this, new PipelineStatusReport(newState, progressPercentage, message, logLevel, error));
        }

        public void StartRecording(RecordingRequest request)
        {
            lock (_stateLock)
            {
                if (_state != PipelineLifecycleState.Idle) return;
                _state = PipelineLifecycleState.Recording;
                _activeMode = (InternalMode)(int)request.Mode;
                ActiveMode = request.Mode;
                ActiveSource = request.Source;
            }

            bool startedSuccessfully = false;
            try
            {
                var settings = AppSettings.Load();
                if (string.IsNullOrEmpty(settings.MicId) && request.Source == AudioSource.Microphone)
                {
                    TransitionTo(PipelineLifecycleState.Failed, -1.0, "ErrMicUnplugged", DiagnosticLogger.Level.ERROR, PipelineError.MicDisconnected);
                    ErrorOccurred?.Invoke(this, "ErrMicUnplugged");
                    return;
                }
                if (string.IsNullOrEmpty(settings.LastModelPath) || !File.Exists(settings.LastModelPath))
                {
                    TransitionTo(PipelineLifecycleState.Failed, -1.0, "ModelMissing", DiagnosticLogger.Level.ERROR, PipelineError.ModelMissing);
                    MissingModelRequested?.Invoke(this, EventArgs.Empty);
                    return;
                }

                _currentTranslate = request.Mode == ProcessingMode.Translate || request.Mode == ProcessingMode.Prompt;
                _currentLang      = request.Mode == ProcessingMode.Primary ? settings.LanguagePrimary : "en";

                try { if (File.Exists(_tempWavPath)) File.Delete(_tempWavPath); } catch { }
                _activeCapture = request.Source == AudioSource.Loopback ? _loopbackCapture : _micCapture;

                double silenceTimeout = request.Source == AudioSource.Loopback ? settings.VadSilenceSeconds + 3.0 : settings.VadSilenceSeconds;
                bool enableVad = !settings.IsPushToTalkEnabled;

                bool started = _activeCapture.StartRecording(settings.MicId, _tempWavPath, settings.VadThreshold, silenceTimeout, enableVad);
                if (!started)
                {
                    TransitionTo(PipelineLifecycleState.Failed, -1.0, "ErrMicUnplugged", DiagnosticLogger.Level.ERROR, PipelineError.MicDisconnected);
                    ErrorOccurred?.Invoke(this, "ErrMicUnplugged");
                    return;
                }

                if (settings.SoundNotifications) System.Media.SystemSounds.Beep.Play();
                StartTimer();

                TransitionTo(PipelineLifecycleState.Recording, 0.0, "Recording started");
                StateChanged?.Invoke(this, new RecordingStateChangedEventArgs(RecordingState.Recording, request.Source, request.Mode));
                startedSuccessfully = true;
            }
            catch (Exception ex)
            {
                TransitionTo(PipelineLifecycleState.Failed, -1.0, ex.Message, DiagnosticLogger.Level.ERROR, PipelineError.RecordingAborted);
                ErrorOccurred?.Invoke(this, ex.Message);
            }
            finally
            {
                // ── Guarantee: if start fails for any reason (including
                //    an exception before TransitionTo) — state is forcibly reset to Idle.
                if (!startedSuccessfully)
                {
                    lock (_stateLock)
                    {
                        if (_state != PipelineLifecycleState.Idle)
                            _state = PipelineLifecycleState.Idle;
                        _activeMode  = InternalMode.None;
                        ActiveMode   = ProcessingMode.Primary;
                        ActiveSource = AudioSource.Microphone;
                    }
                    _activeCapture = _micCapture;
                    StateChanged?.Invoke(this, new RecordingStateChangedEventArgs(RecordingState.Idle));
                }
            }
        }


        public async Task StopAndProcessAsync(AppSettings settings, Func<string, string> getResource)
        {
            InternalMode mode;
            lock (_stateLock)
            {
                if (_state != PipelineLifecycleState.Recording) return;
                _state = PipelineLifecycleState.ProcessingAudio;
                mode = _activeMode;
                _activeMode = InternalMode.None;
            }

            try
            {
                TransitionTo(PipelineLifecycleState.ProcessingAudio, 10.0, getResource("LblProcessing"));
                await _activeCapture.StopRecordingAsync();
                StopTimer();
                StateChanged?.Invoke(this, new RecordingStateChangedEventArgs(RecordingState.Processing));

                if (!IsAudioWorthProcessing(_tempWavPath))
                {
                    TransitionTo(PipelineLifecycleState.Idle, 0.0, "Audio below threshold; skipping processing");
                    StateChanged?.Invoke(this, new RecordingStateChangedEventArgs(RecordingState.Idle));
                    return;
                }

                if (settings.SoundNotifications) SystemSounds.Exclamation.Play();

                var lang      = _currentLang;
                var translate = _currentTranslate;

                _whisperCts = new CancellationTokenSource();
                var progress = new Progress<string>(msg => 
                { 
                    if (!string.IsNullOrWhiteSpace(msg)) 
                    {
#pragma warning disable CS0618
                        StatusUpdated?.Invoke(this, msg);
#pragma warning restore CS0618
                        StatusReported?.Invoke(this, new PipelineStatusReport(PipelineLifecycleState.RunningInference, -1.0, msg));
                    } 
                });

                string? targetProfileId = ActiveMode switch
                {
                    ProcessingMode.Primary => settings.PrimaryProfileId,
                    ProcessingMode.Translate => settings.TranslateProfileId,
                    ProcessingMode.Prompt => settings.PromptProfileId,
                    _ => null
                };

                WhisperProfile? activeProfile = string.IsNullOrEmpty(targetProfileId) 
                    ? null 
                    : settings.CustomProfiles?.Find(p => p.Id == targetProfileId);

                string selectedPrompt = activeProfile != null 
                    ? activeProfile.PromptTags 
                    : (mode switch
                    {
                        InternalMode.Translate => settings.PromptTranslate,
                        InternalMode.Prompt    => LoadDictPrompt(settings),
                        _                      => string.Empty
                    });

                double activeTemp = activeProfile != null ? activeProfile.Temperature : settings.Temperature;

                await RunWhisperPipelineAsync(lang, translate, selectedPrompt, activeTemp, progress, settings, getResource, _whisperCts.Token);
            }
            catch (Exception ex)
            {
                var error = PipelineError.RecordingAborted;
                if (ex is NAudio.MmException || ex is System.Runtime.InteropServices.COMException || ex.Message.Contains("disconnected") || ex.Message.Contains("unplugged"))
                {
                    error = PipelineError.MicDisconnected;
                    try { TransientDataCleaner.Cleanup(_tempWavPath, "", ""); } catch { }
                }
                TransitionTo(PipelineLifecycleState.Failed, -1.0, ex.Message, DiagnosticLogger.Level.ERROR, error);
                ErrorOccurred?.Invoke(this, ex.Message);
            }
            finally
            {
                TransitionTo(PipelineLifecycleState.Idle, 0.0, "Pipeline released to Idle");
                lock (_stateLock)
                {
                    _activeMode = InternalMode.None;
                    ActiveMode = ProcessingMode.Primary;
                    ActiveSource = AudioSource.Microphone;
                }
                _activeCapture = _micCapture;
                StateChanged?.Invoke(this, new RecordingStateChangedEventArgs(RecordingState.Idle));
            }
        }

        public void CancelWhisper() => _whisperCts?.Cancel();

        public async Task HandleHotkeyTrigger(ProcessingMode mode, AudioSource source, bool isPushToTalk, bool isKeyDown, AppSettings settings, Func<string, string> getResource)
        {
            if (isPushToTalk)
            {
                if (isKeyDown)
                {
                    if (!IsRecording && !IsProcessing)
                        StartRecording(new RecordingRequest(mode, source));
                }
                else
                {
                    if (IsRecording)
                        await StopAndProcessAsync(settings, getResource);
                }
            }
            else // Toggle Mode
            {
                if (isKeyDown)
                {
                    if (IsProcessing) return;

                    if (IsRecording)
                        await StopAndProcessAsync(settings, getResource);
                    else
                        StartRecording(new RecordingRequest(mode, source));
                }
            }
        }

        private async Task RunWhisperPipelineAsync(string lang, bool isTranslate, string techPrompt, double temperature, IProgress<string> progress, AppSettings settings, Func<string, string> getResource, CancellationToken token)
        {
            try
            {
                string ramFmt  = getResource("ErrLowRam");
                var (ramOk, ramMsg) = await _hardware.CheckRamAsync(ramFmt);
                if (!ramOk)
                {
                    TransitionTo(PipelineLifecycleState.Failed, -1.0, ramMsg, DiagnosticLogger.Level.ERROR, PipelineError.LowMemoryFallback);
                    ErrorOccurred?.Invoke(this, ramMsg);
                    return;
                }

                string model = AppSettings.Load().LastModelPath;
                if (string.IsNullOrEmpty(model) || !File.Exists(model))
                {
                    DiagnosticLogger.Instance.Error("RecordingOrchestrationService", "Model file missing before inference: " + model);
                    TransitionTo(PipelineLifecycleState.Failed, -1.0, "ModelMissing", DiagnosticLogger.Level.ERROR, PipelineError.ModelMissing);
                    MissingModelRequested?.Invoke(this, EventArgs.Empty);
                    return;
                }

                TransitionTo(PipelineLifecycleState.RunningInference, 30.0, getResource("LblProcessing"));

                string? rawResult = await _whisper.RunAsync(
                    model, lang, isTranslate, techPrompt, progress,
                    msg => DiagnosticLogger.Instance.Info("RecordingOrchestrationService", msg),
                    token,
                    beamSize:           settings.BeamSize,
                    bestOf:             settings.BestOf,
                    temperature:        temperature,
                    noSpeechThreshold:  settings.NoSpeechThreshold,
                    vulkanStatusCallback: isVulkan =>
                    {
                        var status = isVulkan ? VulkanStatus.Active : VulkanStatus.CpuFallback;
                        _hardware.LastVulkanStatus = status;
                        VulkanStatusChecked?.Invoke(this, status);
                        DiagnosticLogger.Instance.Info("HardwareCheck", $"Inference engine acceleration finalized: {(isVulkan ? "VULKAN_GPU_ACTIVE" : "HARDWARE_FALLBACK_TO_CPU")}");
                    });

                if (rawResult is null)
                {
                    TransitionTo(PipelineLifecycleState.Failed, -1.0, "Inference returned null", DiagnosticLogger.Level.WARN);
                    return;
                }

                TransitionTo(PipelineLifecycleState.FilteringHallucinations, 80.0, getResource("MsgHallucinationFiltered"));

                if (!_hallucinationFilter.Check(rawResult, out string cleanResult))
                {
                    TransitionTo(PipelineLifecycleState.Completed, 100.0, getResource("MsgHallucinationFiltered"));
                    return;
                }

                string finalResult = _postProcessor.Process(cleanResult);
                if (string.IsNullOrWhiteSpace(finalResult) || !finalResult.Any(char.IsLetterOrDigit))
                {
                    TransitionTo(PipelineLifecycleState.Completed, 100.0, "Silence / Ignored");
                    return;
                }

                TransitionTo(PipelineLifecycleState.Completed, 100.0, getResource("MsgWhisperDone"));
                TranscriptionCompleted?.Invoke(this, new TranscriptionResultEventArgs(finalResult, lang, isTranslate));
            }
            catch (OperationCanceledException)
            {
                TransitionTo(PipelineLifecycleState.Failed, -1.0, "RecordingAborted", DiagnosticLogger.Level.INFO, PipelineError.RecordingAborted);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Instance.Error("RecordingOrchestrationService", ex, "Pipeline failed");
                TransitionTo(PipelineLifecycleState.Failed, -1.0, ex.Message, DiagnosticLogger.Level.ERROR, PipelineError.RecordingAborted);
                ErrorOccurred?.Invoke(this, ex.Message);
            }
        }

        private bool IsAudioWorthProcessing(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= AudioConstants.WavHeaderBytes) return false;

                byte[]? bytes = null;
                for (int i = 0; i < 3; i++)
                {
                    try { bytes = File.ReadAllBytes(path); break; }
                    catch (Exception ex) { DiagnosticLogger.Instance.Trace("RecordingOrchestrationService", $"Read WAV retry: {ex.Message}"); Thread.Sleep(50); }
                }
                if (bytes == null || bytes.Length <= AudioConstants.WavHeaderBytes) return true;

                int sampleCount = (bytes.Length - AudioConstants.WavHeaderBytes) / 2;
                if (sampleCount < AudioConstants.MinSampleCount) return false;

                int startSample = AudioConstants.EdgeSampleSkip;
                int endSample   = sampleCount - AudioConstants.EdgeSampleSkip;
                if (startSample >= endSample) { startSample = sampleCount / 4; endSample = sampleCount - (sampleCount / 4); }

                int validCount = endSample - startSample;
                if (validCount <= 0) return false;

                long sum = 0;
                for (int i = startSample; i < endSample; i++) sum += BitConverter.ToInt16(bytes, AudioConstants.WavHeaderBytes + i * 2);
                short dcOffset = (short)(sum / validCount);

                short maxAc = 0;
                for (int i = startSample; i < endSample; i++)
                {
                    short ac = (short)Math.Abs(BitConverter.ToInt16(bytes, AudioConstants.WavHeaderBytes + i * 2) - dcOffset);
                    if (ac > maxAc) maxAc = ac;
                }

                DiagnosticLogger.Instance.Info("RecordingOrchestrationService", $"[PCM Filter] DC Offset: {dcOffset} | True AC Peak: {maxAc}");

                // H3: RMS energy is more robust than Peak for silence detection.
                // Peak fires on single spikes; RMS averages across all samples.
                double sumSq = 0;
                for (int i = startSample; i < endSample; i++)
                {
                    double s = BitConverter.ToInt16(bytes, AudioConstants.WavHeaderBytes + i * 2) - dcOffset;
                    sumSq += s * s;
                }
                double rms = Math.Sqrt(sumSq / validCount);

                DiagnosticLogger.Instance.Info("RecordingOrchestrationService", $"[PCM Filter] RMS: {rms:F1}");

                // Loopback: lower RMS threshold (background audio can be quiet)
                // Mic: higher threshold (ambient noise should not trigger processing)
                double threshold = _activeCapture == _loopbackCapture
                    ? AudioConstants.RmsThresholdLoopback
                    : AudioConstants.RmsThresholdMic;

                return rms > threshold;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Instance.Warn("RecordingOrchestrationService", $"[PCM Filter] File error: {ex.Message} -> Fallback to True");
                return true;
            }
        }

        private void StartTimer()
        {
            _recSeconds = 0;
            _recTimer?.Stop();
            _recTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _recTimer.Tick += (_, _) => { _recSeconds++; RecordingTimerTick?.Invoke(this, _recSeconds); };
            _recTimer.Start();
            RecordingTimerTick?.Invoke(this, 0);
        }

        private void StopTimer() { _recTimer?.Stop(); _recTimer = null; _recSeconds = 0; }

        private static string LoadDictPrompt(AppSettings settings)
        {
            try
            {
                string dictDir  = Path.Combine(AppSettings.AppDataDir, "dictionary");
                string dictPath = Path.Combine(dictDir, "dictionary.txt");
                if (!File.Exists(dictPath)) return string.Empty;
                string raw = File.ReadAllText(dictPath).Replace("\r\n", " ").Replace("\n", " ").Replace("\"", "");
                return raw.Length > 250 ? raw[..250] : raw;
            }
            catch (Exception ex) { DiagnosticLogger.Instance.Error("RecordingOrchestrationService", ex, "LoadDictPrompt failed"); return string.Empty; }
        }

        public void Dispose()
        {
            _whisperCts?.Cancel();
            _whisperCts?.Dispose();
            StopTimer();
        }
    }

    public sealed class RecordingRequest
    {
        public ProcessingMode Mode   { get; }
        public AudioSource    Source { get; }
        public RecordingRequest(ProcessingMode mode, AudioSource source) { Mode = mode; Source = source; }
    }
}