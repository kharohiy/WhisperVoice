using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using WhisperVoice;

namespace WhisperVoice.Services
{
    public interface IMainWindowView
    {
        string TempWavPath { get; }
        string TempTxtPath { get; }
        string DictDir { get; }
        string AppDataDir { get; }

        void InvokeOnUi(Action action);
        Task InvokeOnUiAsync(Func<Task> asyncAction);

        void ShowErrorPopup(string resourceKey, string? details = null);
        void UpdateMicLabel(string text, bool ok);
        void SetupVolumeSlider(float volume);
        void SyncVolumeFromSystem(float volume);
        void UpdateLanguageButton(string langName, string? activeKey = null);
        void UpdateTopBar(bool isPtt, string defaultText, string privacyTooltip);
        
        void ShowProcessingPanel(bool show);
        void SetStatusText(string text);
        void SetMicNameText(string text, bool isErrorOrProcessing);
        
        void StartVadAnimation();
        void StopVadAnimation();
        void SetVuMeterValue(double value);
        
        void SetHeroButtonState(string resourceKey, bool isEnabled, string backgroundColorHex, string foregroundColorHex);
        
        void SetRecordingState(bool isRecording);
        void SetProcessingState();
        void SetIdleState();
        void UpdateTimer(string text);
        void ShowMissingModelWindow();
        void ToggleNotepad();
        void ShowCopyFeedback();
        void ShowAndActivate();
        void HideWindow();
        bool IsWindowVisible { get; }

        string TryGetResource(string key, string fallback);
    }

    public sealed class MainWindowController : IDisposable
    {
        private readonly IMainWindowView _view;

        private readonly AudioCaptureService _microphoneCapture;
        private readonly AudioCaptureService _loopbackCapture;
        private AudioCaptureService _activeCapture;

        private readonly WhisperExecutionService _whisper;
        private readonly HardwareCheckService _hardware;
        private readonly HallucinationFilter _hallucinationFilter;
        private readonly TextPostProcessorService _postProcessor;
        private readonly RecordingOrchestrationService _recorder;

        private readonly ITrayIconService _trayIconService = new TrayIconService();
        private readonly IClipboardService _clipboardService = new ClipboardService();
        private readonly IHistoryService _historyService = new HistoryService();

        private HotkeyRouter? _hotkeyRouter;
        private AppSettings _settings;
        private DateTime _lastAction = DateTime.MinValue;

        public MainWindowController(IMainWindowView view)
        {
            _view = view;
            _settings = AppSettings.Load();

            _whisper = new WhisperExecutionService();
            _hardware = new HardwareCheckService();
            _hallucinationFilter = new HallucinationFilter(_view.DictDir);
            _postProcessor = new TextPostProcessorService();

            _microphoneCapture = new AudioCaptureService(loopbackMode: false);
            _loopbackCapture = new AudioCaptureService(loopbackMode: true);
            _activeCapture = _microphoneCapture;

            _recorder = new RecordingOrchestrationService(
                _microphoneCapture,
                _loopbackCapture,
                _whisper,
                _hardware,
                _hallucinationFilter,
                _postProcessor,
                _view.TempWavPath);

            WireUpEvents();
        }

        public IHistoryService HistoryService => _historyService;

        private void WireUpEvents()
        {
            _recorder.StatusReported += Recorder_StatusReported;
            _recorder.TranscriptionCompleted += Recorder_TranscriptionCompleted;
            _recorder.RecordingTimerTick += Recorder_TimerTick;
            _recorder.MissingModelRequested += (_, _) => _view.InvokeOnUi(() => _view.ShowMissingModelWindow());
            _recorder.ErrorOccurred += (_, key) => _view.ShowErrorPopup(key);
            _recorder.VulkanStatusChecked += Recorder_VulkanStatusChecked;

            WireAudioEvents(_microphoneCapture);
            WireAudioEvents(_loopbackCapture);
        }

        public void Initialize()
        {
            CleanupTempFiles();
            SetupTrayIcon();
            LoadMicFromSettings();
            UpdateLanguageButton();
            UpdateTopBar();
        }

        public void OnVisibleChanged()
        {
            if (_view.IsWindowVisible)
            {
                _view.SyncVolumeFromSystem(_microphoneCapture.GetVolume());
            }
        }

        public void SetVolume(float volume)
        {
            _microphoneCapture.SetVolume(volume);
        }

        private void WireAudioEvents(AudioCaptureService service)
        {
            service.PeakAvailable += val => _view.InvokeOnUi(() => {
                if (_activeCapture == service) _view.SetVuMeterValue(val);
            });

            service.SilenceDetected += () => _view.InvokeOnUiAsync(async () => {
                if (_activeCapture == service) await OnVadSilenceDetectedAsync();
            });

            service.RecordingAborted += OnRecordingAborted;

            if (service == _microphoneCapture)
            {
                service.VolumeChanged += vol => _view.InvokeOnUi(() => _view.SetupVolumeSlider(vol));
                service.DeviceDisconnected += OnDeviceDisconnected;
            }
        }

        private void OnRecordingAborted(Exception ex)
        {
            _view.InvokeOnUiAsync(async () =>
            {
                if (_recorder.IsRecording) await StopAndProcessAsync();
                _view.ShowErrorPopup("ErrRecordingAborted", ex.Message);
            });
        }

        private async Task OnVadSilenceDetectedAsync()
        {
            if (_recorder.IsRecording)
            {
                DiagnosticLogger.Instance.Info("MainWindowController", "VAD: silence threshold reached — auto-stopping.");
                await StopAndProcessAsync();
            }
        }

        private async Task StopAndProcessAsync()
        {
            await _recorder.StopAndProcessAsync(AppSettings.Load(), key => _view.TryGetResource(key, key));
        }

        public async Task ToggleRecordingAsync()
        {
            if (_recorder.IsRecording)
            {
                await StopAndProcessAsync();
            }
            else if (!_recorder.IsProcessing)
            {
                _settings = AppSettings.Load();
                _recorder.StartRecording(new RecordingRequest(ProcessingMode.Primary, AudioSource.Microphone));
            }
        }

        public void CancelRecording()
        {
            _recorder.CancelWhisper();
            _view.SetStatusText(_view.TryGetResource("LblCancelled", "Cancelled"));
        }

        public void HandleDeviceChange()
        {
            if (!_microphoneCapture.IsDeviceAttached && !string.IsNullOrEmpty(_settings.MicId))
            {
                Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    _view.InvokeOnUi(() =>
                    {
                        string? newId = FindDeviceIdByName(_settings.MicName);
                        if (newId != null && _microphoneCapture.AttachDevice(newId))
                        {
                            if (newId != _settings.MicId)
                            {
                                _settings.MicId = newId;
                                _settings.Save();
                            }
                            _view.UpdateMicLabel(_settings.MicName, ok: true);
                            _view.SetupVolumeSlider(_microphoneCapture.GetVolume());
                            _microphoneCapture.RestartSilentCapture();
                        }
                    });
                });
            }
        }

        public void SelectMicrophone(string id, string name)
        {
            _settings.MicId = id;
            _settings.MicName = name;
            _settings.Save();
            _microphoneCapture.AttachDevice(id);
            _view.UpdateMicLabel(name, ok: true);
            _view.SetupVolumeSlider(_microphoneCapture.GetVolume());
        }

        public void RebindHotkeys()
        {
            _settings = AppSettings.Load();

            if (_hotkeyRouter == null)
            {
                var hookProvider = new KeyboardHookProvider();
                _hotkeyRouter = new HotkeyRouter(hookProvider);
                _hotkeyRouter.OnRecordRequested += (s, e) => _view.InvokeOnUiAsync(async () => 
                    await _recorder.HandleHotkeyTrigger(e.Mode, e.Source, _settings.IsPushToTalkEnabled, true, AppSettings.Load(), k => _view.TryGetResource(k, k)));
                _hotkeyRouter.OnRecordStopped += (s, e) => _view.InvokeOnUiAsync(async () => 
                    await _recorder.HandleHotkeyTrigger(e.Mode, e.Source, _settings.IsPushToTalkEnabled, false, AppSettings.Load(), k => _view.TryGetResource(k, k)));
                _hotkeyRouter.OnToggleMenu += (s, e) => { if (!IsSpam()) { if (_view.IsWindowVisible) _view.HideWindow(); else _view.ShowAndActivate(); } };
                _hotkeyRouter.OnOpenNotepad += (s, e) => { if (!IsSpam()) _view.ToggleNotepad(); };
            }

            _hotkeyRouter.RebindHotkeys(_settings);
        }

        public void UpdateLanguageButton()
        {
            _settings = AppSettings.Load();
            string langKey = _settings.LanguagePrimary switch
            {
                "en" => "LangNameEn", "uk" => "LangNameUk", "pl" => "LangNamePl",
                "de" => "LangNameDe", "es" => "LangNameEs", "fr" => "LangNameFr",
                _ => "LangNameRu"
            };
            string langName = _view.TryGetResource(langKey, _settings.LanguagePrimary.ToUpper());
            _view.UpdateLanguageButton(langName, null);
        }

        public void UpdateTopBar()
        {
            _settings = AppSettings.Load();
            string defaultModeText = _settings.IsPushToTalkEnabled ? "Push-To-Talk" : "Toggle";
            string modeText = _view.TryGetResource(_settings.IsPushToTalkEnabled ? "ModePushToTalk" : "ModeToggle", defaultModeText);
            string privacyTooltip = _view.TryGetResource("ToolTipPrivacyPolicy", "Audio is temporarily stored locally before transcription and deleted upon exit.");
            _view.UpdateTopBar(_settings.IsPushToTalkEnabled, modeText, privacyTooltip);
        }

        private void Recorder_StatusReported(object? sender, PipelineStatusReport e)
        {
            _view.InvokeOnUi(() =>
            {
                if (!string.IsNullOrWhiteSpace(e.Message) && e.State != PipelineLifecycleState.Idle)
                {
                    _view.SetStatusText(e.Message);
                }

                switch (e.State)
                {
                    case PipelineLifecycleState.Recording:
                        _view.StartVadAnimation();
                        string keyBase = _recorder.ActiveMode switch
                        {
                            ProcessingMode.Primary => _settings.HotkeyPrimary,
                            ProcessingMode.Translate => _settings.HotkeyTranslate,
                            _ => _settings.HotkeyPrompt
                        };
                        string keySig = _recorder.ActiveSource == AudioSource.Loopback ? "Ctrl+" + keyBase : keyBase;
                        
                        _view.UpdateLanguageButton(_view.TryGetResource("LangNameEn", "EN"), keySig); // Simplified
                        _view.SetMicNameText(_view.TryGetResource("LblRecording", "Recording") + " 0:00", true);
                        _view.SetHeroButtonState("BtnHeroRecording", true, "#DC143C", "#FFFFFF"); // Crimson
                        _view.SetRecordingState(true);
                        _trayIconService.SetRecordingState(true);
                        break;

                    case PipelineLifecycleState.ProcessingAudio:
                    case PipelineLifecycleState.RunningInference:
                    case PipelineLifecycleState.FilteringHallucinations:
                        _view.StopVadAnimation();
                        _view.SetMicNameText(_view.TryGetResource("LblProcessing", "Processing…"), true);
                        UpdateLanguageButton();
                        _view.SetVuMeterValue(0);
                        _view.SetProcessingState();
                        _view.SetHeroButtonState("BtnHeroProcessing", false, "#3A3A3A", "#FFA500"); // Orange
                        break;

                    case PipelineLifecycleState.Idle:
                    case PipelineLifecycleState.Completed:
                    case PipelineLifecycleState.Failed:
                        _view.StopVadAnimation();
                        _view.SetIdleState();
                        UpdateLanguageButton();
                        _trayIconService.SetRecordingState(false);
                        LoadMicFromSettings();
                        _view.SetHeroButtonState("BtnHeroIdle", true, "#1565C0", "#FFFFFF");
                        break;
                }
            });
        }

        private void Recorder_TranscriptionCompleted(object? sender, TranscriptionResultEventArgs e)
        {
            _view.InvokeOnUiAsync(async () =>
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
            _view.InvokeOnUi(() =>
            {
                int m = seconds / 60;
                int s = seconds % 60;
                string recLabel = _view.TryGetResource("LblRecording", "Recording");
                _view.UpdateTimer($"{recLabel} {m}:{s:D2}");
            });
        }

        private void Recorder_VulkanStatusChecked(object? sender, VulkanStatus status)
        {
            _view.InvokeOnUi(() =>
            {
                if (status == VulkanStatus.CpuFallback)
                {
                    _view.SetStatusText(_view.TryGetResource("MsgVulkanCpuFallback", "Warning: Inference is running on CPU. Check Vulkan support."));
                }
            });
        }

        private void OnDeviceDisconnected()
        {
            _view.InvokeOnUiAsync(async () =>
            {
                if (_recorder.IsRecording) await StopAndProcessAsync();
                _view.ShowErrorPopup("ErrMicUnplugged");
                _view.UpdateMicLabel(_view.TryGetResource("LblNoMicSelected", "⚠️ SELECT A MICROPHONE!"), ok: false);
            });
        }

        private string? FindDeviceIdByName(string targetName)
        {
            if (string.IsNullOrEmpty(targetName)) return null;
            try
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active);
                foreach (var device in devices) if (device.FriendlyName == targetName) return device.ID;
            }
            catch (Exception ex) { DiagnosticLogger.Instance.Warn("MainWindowController", $"Operation failed: {ex.Message}"); }
            return null;
        }

        private void LoadMicFromSettings()
        {
            if (_settings.HasMic)
            {
                bool attached = _microphoneCapture.AttachDevice(_settings.MicId);
                _view.UpdateMicLabel(_settings.MicName, ok: attached);
                if (attached) _view.SetupVolumeSlider(_microphoneCapture.GetVolume());
            }
            else
            {
                _view.UpdateMicLabel(_view.TryGetResource("LblNoMicSelected", "⚠️ SELECT A MICROPHONE!"), ok: false);
            }
        }

        private void SetupTrayIcon()
        {
            _trayIconService.Initialize(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WhisperVoice.ico"),
                "Whisper Voice",
                _view.TryGetResource("TrayMenuControl", "Control Panel"),
                _view.TryGetResource("TrayMenuNotepad", "Notepad"),
                _view.TryGetResource("TrayMenuExit", "Exit")
            );
            
            _trayIconService.OnRestoreRequested += (_, _) => _view.ShowAndActivate();
            _trayIconService.OnNotepadRequested += (_, _) => _view.ToggleNotepad();
            _trayIconService.OnExitRequested += (_, _) =>
            {
                Dispose();
                System.Windows.Application.Current.Shutdown();
            };
        }

        private void CleanupTempFiles()
        {
            TransientDataCleaner.Cleanup(
                _view.TempWavPath, 
                _view.TempTxtPath, 
                AppSettings.ModelsDir,
                onError: (msg, ex) => DiagnosticLogger.Instance.Warn("MainWindowController", $"{msg}: {ex.Message}"),
                onInfo: msg => DiagnosticLogger.Instance.Info("MainWindowController", msg));
        }

        private bool IsSpam()
        {
            var diff = (DateTime.Now - _lastAction).TotalMilliseconds;
            _lastAction = DateTime.Now;
            return diff < 600;
        }

        public void Dispose()
        {
            try
            {
                _recorder?.Dispose();
                _microphoneCapture?.Dispose();
                _loopbackCapture?.Dispose();
                _hotkeyRouter?.Dispose();
                _trayIconService?.Dispose();
                CleanupTempFiles();
            }
            catch (Exception ex) { DiagnosticLogger.Instance.Warn("MainWindowController", $"Operation failed: {ex.Message}"); }
        }
    }
}
