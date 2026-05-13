using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Runtime.InteropServices;

namespace WhisperVoice.Services
{
    public class AudioCaptureService : IDisposable
    {
        private static readonly DiagnosticLogger Log = DiagnosticLogger.Instance;
        private const string Comp = "AudioCaptureService";

        private readonly AudioRecorder _recorder = new();
        private WasapiCapture? _silentCapture;
        private MMDevice? _device;
        private DateTime _lastSilentPeak = DateTime.MinValue;

        public bool IsRecording => _recorder.IsRecording;

        public bool IsDeviceAttached
        {
            get
            {
                if (_device == null) return false;

                try
                {
                    var state = _device.State;
                    if (state != DeviceState.Active)
                    {
                        Log.Warn(Comp, $"IsDeviceAttached: device state={state} — detaching");
                        DetachDevice();
                        return false;
                    }
                    return true;
                }
                catch (COMException ex)
                {
                    Log.Error(Comp, ex, "IsDeviceAttached COMException — detaching");
                    DetachDevice();
                    return false;
                }
                catch (Exception ex)
                {
                    Log.Error(Comp, ex, "IsDeviceAttached unexpected exception");
                    return false;
                }
            }
        }

        public event Action<double>? PeakAvailable;
        public event Action? SilenceDetected;
        public event Action<float>? VolumeChanged;
        public event Action? DeviceDisconnected;

        public AudioCaptureService()
        {
            _recorder.PeakAvailable += val => PeakAvailable?.Invoke(val);
            _recorder.SilenceDetected += () => SilenceDetected?.Invoke();
            _recorder.RecordingAborted += OnRecordingAborted;

            Log.Info(Comp, "AudioCaptureService constructed — event wiring complete");
        }

        public bool AttachDevice(string micId)
        {
            Log.Info(Comp, $"AttachDevice called  micId={micId}");

            try
            {
                DetachDevice();

                using var enumerator = new MMDeviceEnumerator();
                _device = enumerator.GetDevice(micId);

                Log.Info(Comp, $"Device resolved: FriendlyName=\"{_device.FriendlyName}\"  " +
                               $"State={_device.State}  ID={_device.ID}");

                if (_device.State != DeviceState.Active)
                {
                    Log.Warn(Comp, $"AttachDevice: device not Active (State={_device.State}) — aborting");
                    _device = null;
                    return false;
                }

                _silentCapture = new WasapiCapture(_device, true, 50);
                _silentCapture.DataAvailable += SilentCapture_DataAvailable;
                _silentCapture.StartRecording();

                _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
                float vol = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
                VolumeChanged?.Invoke(vol);

                Log.Info(Comp, $"AttachDevice SUCCESS  " +
                               $"SilentCapture={_silentCapture.WaveFormat.SampleRate}Hz  " +
                               $"MasterVolume={vol:P0}");
                return true;
            }
            catch (COMException ex)
            {
                Log.Error(Comp, ex, "AttachDevice COMException");
                HandleDeviceFailure();
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(Comp, ex, "AttachDevice unexpected exception");
                return false;
            }
        }

        private void DetachDevice()
        {
            Log.Trace(Comp, $"DetachDevice called  _device={(_device != null ? "EXISTS" : "NULL")}");

            if (_device != null)
            {
                try { _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; } catch { }
                _device = null;
            }

            try
            {
                if (_silentCapture != null)
                {
                    _silentCapture.StopRecording();
                    _silentCapture.Dispose();
                    _silentCapture = null;
                    Log.Trace(Comp, "DetachDevice: silent capture stopped and disposed");
                }
            }
            catch (Exception ex)
            {
                Log.Warn(Comp, $"DetachDevice: exception disposing silent capture: {ex.Message}");
            }
        }

        private void OnVolumeNotification(AudioVolumeNotificationData data)
            => VolumeChanged?.Invoke(data.MasterVolume);

        public float GetVolume()
        {
            try { return _device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0f; }
            catch (COMException ex)
            {
                Log.Error(Comp, ex, "GetVolume COMException");
                HandleDeviceFailure();
                return 0f;
            }
            catch { return 0f; }
        }

        public void SetVolume(float scalar)
        {
            try { if (_device != null) _device.AudioEndpointVolume.MasterVolumeLevelScalar = scalar; }
            catch (COMException ex)
            {
                Log.Error(Comp, ex, "SetVolume COMException");
                HandleDeviceFailure();
            }
            catch { }
        }

        private void HandleDeviceFailure()
        {
            Log.Error(Comp, "HandleDeviceFailure called — raising DeviceDisconnected");
            DetachDevice();
            DeviceDisconnected?.Invoke();
        }

        private void SilentCapture_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_recorder.IsRecording) return;

            var now = DateTime.UtcNow;
            if ((now - _lastSilentPeak).TotalMilliseconds < 40) return;
            _lastSilentPeak = now;

            if (_silentCapture != null && _device != null)
            {
                try
                {
                    double peak = AudioRecorder.CalculatePeak(e.Buffer, e.BytesRecorded, _silentCapture.WaveFormat);
                    PeakAvailable?.Invoke(peak);
                }
                catch (Exception ex)
                {
                    Log.Warn(Comp, $"SilentCapture peak calc failed: {ex.Message}");
                }
            }
            else
            {
                // This fires when the silent capture's DataAvailable is raised
                // AFTER the device/capture was nulled — a race condition sign.
                Log.Error(Comp,
                    $"SilentCapture_DataAvailable fired but state is NULL  " +
                    $"_silentCapture={((_silentCapture == null) ? "NULL" : "EXISTS")}  " +
                    $"_device={((_device == null) ? "NULL" : "EXISTS")}");
            }
        }

        public bool StartRecording(string micId, string outputPath, double vadThreshold, double vadSilenceSeconds)
        {
            Log.Info(Comp, $"StartRecording  micId={micId}  out={outputPath}  " +
                           $"vadThreshold={vadThreshold}  vadSilence={vadSilenceSeconds}s");

            try { _silentCapture?.StopRecording(); } catch { }

            _recorder.VadEnabled = true;
            _recorder.VadThreshold = vadThreshold;
            _recorder.VadSilenceTimeout = TimeSpan.FromSeconds(vadSilenceSeconds);
            bool result = _recorder.StartRecording(micId, outputPath);

            Log.Info(Comp, $"StartRecording result={result}");
            return result;
        }

        public async System.Threading.Tasks.Task StopRecordingAsync()
        {
            Log.Info(Comp, "StopRecordingAsync called");

            _recorder.VadEnabled = false;
            await _recorder.StopRecordingAsync();

            Log.Info(Comp, "StopRecordingAsync: recorder stopped — restarting silent capture");

            try
            {
                if (_silentCapture != null && _device != null)
                    _silentCapture.StartRecording();
            }
            catch (Exception ex)
            {
                Log.Warn(Comp, $"StopRecordingAsync: failed to restart silent capture ({ex.Message}) — calling RestartSilentCapture");
                RestartSilentCapture();
            }
        }

        public void StopRecordingSync()
        {
            Log.Info(Comp, "StopRecordingSync called");

            _recorder.VadEnabled = false;
            _recorder.StopRecording();

            try
            {
                if (_silentCapture != null && _device != null)
                    _silentCapture.StartRecording();
            }
            catch (Exception ex)
            {
                Log.Warn(Comp, $"StopRecordingSync: failed to restart silent capture ({ex.Message}) — calling RestartSilentCapture");
                RestartSilentCapture();
            }
        }

        public void Dispose()
        {
            Log.Info(Comp, "Dispose called");
            DetachDevice();
            _recorder.StopRecording();
        }

        public void RestartSilentCapture()
        {
            Log.Info(Comp,
                $"RestartSilentCapture called  IsRecording={IsRecording}  " +
                $"_device={(_device != null ? "EXISTS" : "NULL")}");

            if (IsRecording || _device == null)
            {
                Log.Warn(Comp, $"RestartSilentCapture SKIPPED  reason={(IsRecording ? "IsRecording=true" : "_device=NULL")}");
                return;
            }

            try
            {
                Log.Trace(Comp, "RestartSilentCapture: stopping old capture...");
                _silentCapture?.StopRecording();
                _silentCapture?.Dispose();
                _silentCapture = null;

                Log.Trace(Comp, "RestartSilentCapture: creating new WasapiCapture...");
                _silentCapture = new WasapiCapture(_device, true, 50);
                _silentCapture.DataAvailable += SilentCapture_DataAvailable;

                Log.Trace(Comp, "RestartSilentCapture: starting...");
                _silentCapture.StartRecording();

                Log.Info(Comp, "RestartSilentCapture SUCCESS");
            }
            catch (Exception ex)
            {
                Log.Error(Comp, ex, "RestartSilentCapture FAILED");
            }
        }

        // ── BUG-1 FIX (WASAPI vector) ─────────────────────────────────────────
        private void OnRecordingAborted(Exception ex)
        {
            const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
            // Known soft abort HRESULTs for reference:
            // 0x88890010 = AUDCLNT_E_DEVICE_IN_USE     (browser / format change)
            // 0x88890008 = AUDCLNT_E_SERVICE_NOT_RUNNING
            // 0x88890020 = AUDCLNT_E_UNSUPPORTED_FORMAT

            string hresultName = ex.HResult switch
            {
                unchecked((int)0x88890004) => "AUDCLNT_E_DEVICE_INVALIDATED",
                unchecked((int)0x88890010) => "AUDCLNT_E_DEVICE_IN_USE",
                unchecked((int)0x88890008) => "AUDCLNT_E_SERVICE_NOT_RUNNING",
                unchecked((int)0x88890020) => "AUDCLNT_E_UNSUPPORTED_FORMAT",
                _ => "UNKNOWN"
            };

            Log.Error(Comp,
                $"OnRecordingAborted  HRESULT=0x{ex.HResult:X8} ({hresultName})  " +
                $"Message={ex.Message}");

            if (ex.HResult == AUDCLNT_E_DEVICE_INVALIDATED)
            {
                Log.Error(Comp, "OnRecordingAborted: HARD device invalidation — raising DeviceDisconnected");
                HandleDeviceFailure();
            }
            else
            {
                Log.Warn(Comp, "OnRecordingAborted: SOFT abort — attempting RestartSilentCapture");
                try
                {
                    RestartSilentCapture();
                    Log.Info(Comp, "OnRecordingAborted: silent capture restart succeeded");
                }
                catch (Exception restartEx)
                {
                    Log.Error(Comp, restartEx, "OnRecordingAborted: silent capture restart FAILED");
                }
            }
        }
        // ─────────────────────────────────────────────────────────────────────
    }
}
