using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Runtime.InteropServices;

namespace WhisperVoice.Services
{
    public class AudioCaptureService : IDisposable
    {
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
                    // Check if device is actually active (not unplugged)
                    var state = _device.State;
                    if (state != DeviceState.Active)
                    {
                        System.Diagnostics.Debug.WriteLine($"[IsDeviceAttached] Device state is {state}, cleaning up");
                        DetachDevice();
                        return false;
                    }
                    return true;
                }
                catch (COMException ex)
                {
                    // Device was unplugged - COM object invalid
                    System.Diagnostics.Debug.WriteLine($"[IsDeviceAttached] COMException: {ex.Message}, cleaning up");
                    DetachDevice();
                    return false;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[IsDeviceAttached] Exception: {ex.Message}");
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
        }

        public bool AttachDevice(string micId)
        {
            try
            {
                DetachDevice();

                // CRITICAL: Always create fresh enumerator to invalidate stale COM cache
                using var enumerator = new MMDeviceEnumerator();
                _device = enumerator.GetDevice(micId);

                if (_device.State != DeviceState.Active)
                {
                    _device = null;
                    return false;
                }

                _silentCapture = new WasapiCapture(_device, true, 50);
                _silentCapture.DataAvailable += SilentCapture_DataAvailable;
                _silentCapture.StartRecording();

                _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;

                VolumeChanged?.Invoke(_device.AudioEndpointVolume.MasterVolumeLevelScalar);

                return true;
            }
            catch (COMException ex)
            {
                System.Diagnostics.Debug.WriteLine($"AttachDevice COMException: {ex.Message}");
                HandleDeviceFailure();
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AttachDevice Exception: {ex.Message}");
                return false;
            }
        }

        private void DetachDevice()
        {
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
                }
            }
            catch { }
        }

        private void OnVolumeNotification(AudioVolumeNotificationData data)
            => VolumeChanged?.Invoke(data.MasterVolume);

        public float GetVolume()
        {
            try { return _device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0f; }
            catch (COMException) { HandleDeviceFailure(); return 0f; }
            catch { return 0f; }
        }

        public void SetVolume(float scalar)
        {
            try { if (_device != null) _device.AudioEndpointVolume.MasterVolumeLevelScalar = scalar; }
            catch (COMException) { HandleDeviceFailure(); }
            catch { }
        }

        private void HandleDeviceFailure()
        {
            DetachDevice();
            DeviceDisconnected?.Invoke();
        }

        private void SilentCapture_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_recorder.IsRecording) return;

            var now = DateTime.UtcNow;
            if ((now - _lastSilentPeak).TotalMilliseconds < 40) return;
            _lastSilentPeak = now;

            // Safety: ensure capture is still valid
            if (_silentCapture != null && _device != null)
            {
                try
                {
                    double peak = AudioRecorder.CalculatePeak(e.Buffer, e.BytesRecorded, _silentCapture.WaveFormat);
                    PeakAvailable?.Invoke(peak);

                    // Log only occasionally to avoid spam
                    if (peak > 0.01)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SilentCapture] Peak={peak:F3}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SilentCapture] Peak calculation failed: {ex.Message}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[SilentCapture] DataAvailable fired but capture/device is NULL");
            }
        }

        public bool StartRecording(string micId, string outputPath, double vadThreshold, double vadSilenceSeconds)
        {
            try { _silentCapture?.StopRecording(); } catch { }

            _recorder.VadEnabled = true;
            _recorder.VadThreshold = vadThreshold;
            _recorder.VadSilenceTimeout = TimeSpan.FromSeconds(vadSilenceSeconds);
            return _recorder.StartRecording(micId, outputPath);
        }

        public async System.Threading.Tasks.Task StopRecordingAsync()
        {
            _recorder.VadEnabled = false;
            await _recorder.StopRecordingAsync();

            // Try to restart silent capture, recreate if failed
            try
            {
                if (_silentCapture != null && _device != null)
                {
                    _silentCapture.StartRecording();
                }
            }
            catch
            {
                // Stale capture - recreate
                RestartSilentCapture();
            }
        }

        public void StopRecordingSync()
        {
            _recorder.VadEnabled = false;
            _recorder.StopRecording();

            // Try to restart silent capture, recreate if failed
            try
            {
                if (_silentCapture != null && _device != null)
                {
                    _silentCapture.StartRecording();
                }
            }
            catch
            {
                // Stale capture - recreate
                RestartSilentCapture();
            }
        }

        public void Dispose()
        {
            DetachDevice();
            _recorder.StopRecording();
        }

        /// <summary>Force restart silent capture (call after USB reconnect when idle)</summary>
        public void RestartSilentCapture()
        {
            System.Diagnostics.Debug.WriteLine($"[RestartSilentCapture] Called. IsRecording={IsRecording}, _device={((_device != null) ? "EXISTS" : "NULL")}");

            if (IsRecording || _device == null)
            {
                System.Diagnostics.Debug.WriteLine($"[RestartSilentCapture] Skipped (recording or no device)");
                return;
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($"[RestartSilentCapture] Stopping old capture...");
                _silentCapture?.StopRecording();
                _silentCapture?.Dispose();
                _silentCapture = null;

                System.Diagnostics.Debug.WriteLine($"[RestartSilentCapture] Creating new WasapiCapture...");
                _silentCapture = new WasapiCapture(_device, true, 50);
                _silentCapture.DataAvailable += SilentCapture_DataAvailable;

                System.Diagnostics.Debug.WriteLine($"[RestartSilentCapture] Starting recording...");
                _silentCapture.StartRecording();

                System.Diagnostics.Debug.WriteLine($"[RestartSilentCapture] SUCCESS - silent capture restarted");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RestartSilentCapture] FAILED: {ex.Message}");
            }
        }
    }
}