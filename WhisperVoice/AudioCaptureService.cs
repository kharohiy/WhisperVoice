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
        public bool IsDeviceAttached => _device != null;

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

                // ВАЖНО: Всегда создаем новый энумератор, чтобы сбросить мертвый COM-кэш!
                // Именно это происходит, когда ты открываешь свои настройки.
                using var enumerator = new MMDeviceEnumerator();
                _device = enumerator.GetDevice(micId);

                if (_device.State != DeviceState.Active) return false;

                _silentCapture = new WasapiCapture(_device, true, 50);
                _silentCapture.DataAvailable += SilentCapture_DataAvailable;
                _silentCapture.StartRecording();

                _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;

                VolumeChanged?.Invoke(_device.AudioEndpointVolume.MasterVolumeLevelScalar);

                return true;
            }
            catch (COMException)
            {
                HandleDeviceFailure();
                return false;
            }
            catch
            {
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

            if (_silentCapture != null)
            {
                try
                {
                    double peak = AudioRecorder.CalculatePeak(e.Buffer, e.BytesRecorded, _silentCapture.WaveFormat);
                    PeakAvailable?.Invoke(peak);
                }
                catch { }
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
            try { _silentCapture?.StartRecording(); } catch { }
        }

        public void StopRecordingSync()
        {
            _recorder.VadEnabled = false;
            _recorder.StopRecording();
            try { _silentCapture?.StartRecording(); } catch { }
        }

        public void Dispose()
        {
            DetachDevice();
            _recorder.StopRecording();
        }
    }
}