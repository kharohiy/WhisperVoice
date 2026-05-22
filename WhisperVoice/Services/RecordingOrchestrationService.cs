using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperVoice.Services
{
    public enum RecordingState { Idle, Recording, Processing }

    public sealed class RecordingStateChangedEventArgs : EventArgs
    {
        public RecordingState State { get; }
        public AudioSource Source { get; }
        public ProcessingMode Mode { get; }
        public int TimerSeconds { get; }

        public RecordingStateChangedEventArgs(RecordingState state, AudioSource source = AudioSource.Microphone, ProcessingMode mode = ProcessingMode.Primary, int timerSeconds = 0)
        {
            State = state;
            Source = source;
            Mode = mode;
            TimerSeconds = timerSeconds;
        }
    }

    public sealed class TranscriptionResultEventArgs : EventArgs
    {
        public string Text { get; }
        public string Lang { get; }
        public bool IsTranslate { get; }

        public TranscriptionResultEventArgs(string text, string lang, bool isTranslate)
        {
            Text = text;
            Lang = lang;
            IsTranslate = isTranslate;
        }
    }

    public sealed class RecordingOrchestrationService : IDisposable
    {
        public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;
        public event EventHandler<TranscriptionResultEventArgs>? TranscriptionCompleted;
        public event EventHandler<PipelineStatusReport>? StatusReported;
        public event EventHandler<int>? RecordingTimerTick;
        public event EventHandler? MissingModelRequested;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<VulkanStatus>? VulkanStatusChecked;

        private readonly IAudioCaptureManager _captureManager;
        private readonly IWhisperInferenceRunner _inferenceRunner;
        private readonly string _tempWavPath;

        private enum InternalMode { None, Primary, Translate, Prompt }
        
        private readonly object _stateLock = new();
        private PipelineLifecycleState _state = PipelineLifecycleState.Idle;
        private InternalMode _activeMode = InternalMode.None;

        private string _currentLang = "ru";
        private bool _currentTranslate;
        private CancellationTokenSource? _whisperCts;
        private System.Windows.Threading.DispatcherTimer? _recTimer;
        private int _recSeconds;

        public PipelineLifecycleState CurrentState
        {
            get { lock (_stateLock) { return _state; } }
        }

        public bool IsProcessing => CurrentState != PipelineLifecycleState.Idle && CurrentState != PipelineLifecycleState.Recording;
        public bool IsRecording => CurrentState == PipelineLifecycleState.Recording;

        public ProcessingMode ActiveMode { get; private set; } = ProcessingMode.Primary;
        public AudioSource ActiveSource { get; private set; } = AudioSource.Microphone;

        public RecordingOrchestrationService(
            IAudioCaptureService micCapture, 
            IAudioCaptureService loopbackCapture, 
            IWhisperExecutionService whisper, 
            HardwareCheckService hardware, 
            HallucinationFilter hallucinationFilter, 
            TextPostProcessorService postProcessor, 
            string tempWavPath)
        {
            _captureManager = new AudioCaptureManager(micCapture, loopbackCapture);
            _inferenceRunner = new WhisperInferenceRunner(whisper, hardware, hallucinationFilter, postProcessor);
            _tempWavPath = tempWavPath;
        }

        private void TransitionTo(PipelineLifecycleState newState, double progressPercentage = -1.0, string message = "", DiagnosticLogger.Level logLevel = DiagnosticLogger.Level.INFO, PipelineError error = PipelineError.None)
        {
            lock (_stateLock)
            {
                _state = newState;
            }
            
            if (newState == PipelineLifecycleState.Failed && AppSettings.Load().SoundNotifications)
                System.Media.SystemSounds.Hand.Play();

            string logMsg = $"Transitioned to {newState}. Msg: {message}";
            switch (logLevel)
            {
                case DiagnosticLogger.Level.TRACE: DiagnosticLogger.Instance.Trace("RecordingOrchestrationService", logMsg); break;
                case DiagnosticLogger.Level.INFO: DiagnosticLogger.Instance.Info("RecordingOrchestrationService", logMsg); break;
                case DiagnosticLogger.Level.WARN: DiagnosticLogger.Instance.Warn("RecordingOrchestrationService", logMsg); break;
                case DiagnosticLogger.Level.ERROR: DiagnosticLogger.Instance.Error("RecordingOrchestrationService", logMsg); break;
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
                
                if (string.IsNullOrEmpty(settings.LastModelPath) || !File.Exists(settings.LastModelPath))
                {
                    TransitionTo(PipelineLifecycleState.Failed, -1.0, "ModelMissing", DiagnosticLogger.Level.ERROR, PipelineError.ModelMissing);
                    MissingModelRequested?.Invoke(this, EventArgs.Empty);
                    return;
                }

                _currentTranslate = request.Mode == ProcessingMode.Translate || request.Mode == ProcessingMode.Prompt;
                _currentLang = request.Mode == ProcessingMode.Primary ? settings.LanguagePrimary : "en";

                if (!_captureManager.StartRecording(request.Source, settings, _tempWavPath, out var errorType, out var errorMessage))
                {
                    TransitionTo(PipelineLifecycleState.Failed, -1.0, errorMessage, DiagnosticLogger.Level.ERROR, errorType);
                    ErrorOccurred?.Invoke(this, errorMessage);
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
                if (!startedSuccessfully)
                {
                    ResetToIdle();
                }
            }
        }

        private void ResetToIdle()
        {
            lock (_stateLock)
            {
                if (_state != PipelineLifecycleState.Idle) _state = PipelineLifecycleState.Idle;
                _activeMode = InternalMode.None;
                ActiveMode = ProcessingMode.Primary;
                ActiveSource = AudioSource.Microphone;
            }
            StateChanged?.Invoke(this, new RecordingStateChangedEventArgs(RecordingState.Idle));
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
                await _captureManager.StopRecordingAsync();
                StopTimer();
                StateChanged?.Invoke(this, new RecordingStateChangedEventArgs(RecordingState.Processing));

                if (!_captureManager.IsAudioWorthProcessing(_tempWavPath))
                {
                    TransitionTo(PipelineLifecycleState.Idle, 0.0, "Audio below threshold; skipping processing");
                    return;
                }

                if (settings.SoundNotifications) System.Media.SystemSounds.Asterisk.Play();

                _whisperCts = new CancellationTokenSource();
                var progress = new Progress<string>(msg => 
                { 
                    if (!string.IsNullOrWhiteSpace(msg)) 
                        StatusReported?.Invoke(this, new PipelineStatusReport(PipelineLifecycleState.RunningInference, -1.0, msg));
                });

                var result = await _inferenceRunner.RunPipelineAsync(
                    ActiveMode, _currentLang, _currentTranslate, settings,
                    progress,
                    phase => 
                    {
                        if (phase == "RunningInference") TransitionTo(PipelineLifecycleState.RunningInference, 30.0, getResource("LblProcessing"));
                        else if (phase == "FilteringHallucinations") TransitionTo(PipelineLifecycleState.FilteringHallucinations, 80.0, getResource("MsgHallucinationFiltered"));
                    },
                    status => VulkanStatusChecked?.Invoke(this, status),
                    getResource,
                    _whisperCts.Token
                );

                if (!result.Success)
                {
                    TransitionTo(PipelineLifecycleState.Failed, -1.0, result.ErrorMessage, DiagnosticLogger.Level.ERROR, result.ErrorType);
                    if (result.ErrorType == PipelineError.ModelMissing) MissingModelRequested?.Invoke(this, EventArgs.Empty);
                    else if (result.ErrorType != PipelineError.RecordingAborted) ErrorOccurred?.Invoke(this, result.ErrorMessage);
                    return;
                }

                if (result.IsHallucinationOrSilence)
                {
                    TransitionTo(PipelineLifecycleState.Completed, 100.0, "Silence / Ignored");
                    return;
                }

                TransitionTo(PipelineLifecycleState.Completed, 100.0, getResource("MsgWhisperDone"));
                TranscriptionCompleted?.Invoke(this, new TranscriptionResultEventArgs(result.Text, _currentLang, _currentTranslate));
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
                ResetToIdle();
            }
        }

        public void CancelWhisper() => _whisperCts?.Cancel();

        public async Task HandleHotkeyTrigger(ProcessingMode mode, AudioSource source, bool isPushToTalk, bool isKeyDown, AppSettings settings, Func<string, string> getResource)
        {
            if (isPushToTalk)
            {
                if (isKeyDown) { if (!IsRecording && !IsProcessing) StartRecording(new RecordingRequest(mode, source)); }
                else { if (IsRecording) await StopAndProcessAsync(settings, getResource); }
            }
            else
            {
                if (isKeyDown && !IsProcessing)
                {
                    if (IsRecording) await StopAndProcessAsync(settings, getResource);
                    else StartRecording(new RecordingRequest(mode, source));
                }
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

        public void Dispose()
        {
            _whisperCts?.Cancel();
            _whisperCts?.Dispose();
            StopTimer();
        }
    }

    public sealed class RecordingRequest
    {
        public ProcessingMode Mode { get; }
        public AudioSource Source { get; }
        public RecordingRequest(ProcessingMode mode, AudioSource source) { Mode = mode; Source = source; }
    }
}