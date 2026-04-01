using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;

namespace WhisperVoice.Services
{
    /// <summary>
    /// Manages the active recording capture (via <see cref="AudioRecorder"/>)
    /// and the always-on silent capture used for the VU meter when idle.
    /// Raises events; never touches the UI directly.
    /// </summary>
    public class AudioCaptureService : IDisposable
    {
        private readonly AudioRecorder _recorder = new();
        private WasapiCapture?  _silentCapture;
        private MMDevice?       _device;
        private DateTime        _lastSilentPeak = DateTime.MinValue;

        // ── Public state ───────────────────────────────────────────────────
        public bool IsRecording => _recorder.IsRecording;

        // ── Events ─────────────────────────────────────────────────────────
        /// <summary>Fires on background thread — use Dispatcher.InvokeAsync.</summary>
        public event Action<double>? PeakAvailable;

        /// <summary>Fires on background thread — use Dispatcher.InvokeAsync.</summary>
        public event Action? SilenceDetected;

        // ── Constructor ────────────────────────────────────────────────────
        public AudioCaptureService()
        {
            _recorder.PeakAvailable   += val => PeakAvailable?.Invoke(val);
            _recorder.SilenceDetected += ()  => SilenceDetected?.Invoke();
        }

        // ── Device initialisation ──────────────────────────────────────────
        /// <summary>
        /// Connects to <paramref name="micId"/> and starts the silent VU capture.
        /// Returns <c>false</c> if the device cannot be opened.
        /// </summary>
        public bool AttachDevice(string micId)
        {
            try
            {
                DetachDevice();

                var enumerator = new MMDeviceEnumerator();
                _device = enumerator.GetDevice(micId);

                _silentCapture = new WasapiCapture(_device, true, 50);
                _silentCapture.DataAvailable += SilentCapture_DataAvailable;
                _silentCapture.StartRecording();

                _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
                return true;
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
                _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
                _device = null;
            }

            _silentCapture?.StopRecording();
            _silentCapture?.Dispose();
            _silentCapture = null;
        }

        // ── Volume control ─────────────────────────────────────────────────
        /// <summary>Fires on audio thread — use Dispatcher.InvokeAsync.</summary>
        public event Action<float>? VolumeChanged;

        private void OnVolumeNotification(AudioVolumeNotificationData data)
            => VolumeChanged?.Invoke(data.MasterVolume);

        public float GetVolume()
            => _device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0f;

        public void SetVolume(float scalar)
        {
            if (_device != null)
                _device.AudioEndpointVolume.MasterVolumeLevelScalar = scalar;
        }

        // ── Silent VU capture ──────────────────────────────────────────────
        private void SilentCapture_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_recorder.IsRecording) return;   // active recording owns the meter

            var now = DateTime.UtcNow;
            if ((now - _lastSilentPeak).TotalMilliseconds < 40) return;
            _lastSilentPeak = now;

            if (_silentCapture != null)
            {
                double peak = AudioRecorder.CalculatePeak(
                    e.Buffer, e.BytesRecorded, _silentCapture.WaveFormat);
                PeakAvailable?.Invoke(peak);
            }
        }

        // ── Active recording ───────────────────────────────────────────────
        public void StartRecording(
            string micId, string outputPath,
            double vadThreshold, double vadSilenceSeconds)
        {
            _silentCapture?.StopRecording();

            _recorder.VadEnabled        = true;
            _recorder.VadThreshold      = vadThreshold;
            _recorder.VadSilenceTimeout = TimeSpan.FromSeconds(vadSilenceSeconds);
            _recorder.StartRecording(micId, outputPath);
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

        // ── IDisposable ────────────────────────────────────────────────────
        public void Dispose()
        {
            DetachDevice();
            _recorder.StopRecording();
        }
    }
}
