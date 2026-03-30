using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Threading.Tasks;

namespace WhisperVoice
{
    /// <summary>
    /// Captures microphone audio to a WAV file via WASAPI.
    /// Exposes a peak-level event for the VU-meter and built-in
    /// software Voice Activity Detection (VAD) for auto-stop.
    /// </summary>
    public class AudioRecorder
    {
        private WasapiCapture?   _capture;
        private WaveFileWriter?  _writer;
        private TaskCompletionSource<bool>? _stopTcs;

        private DateTime _lastPeakTime  = DateTime.MinValue;
        private const int PeakIntervalMs = 40;

        // VAD state
        private DateTime _recordingStarted = DateTime.MinValue;
        private DateTime _lastSoundTime    = DateTime.MinValue;
        private bool     _vadSilenceFired  = false;

        public bool IsRecording { get; private set; }

        /// <summary>Fires ~25×/sec with 0-100 peak percentage.</summary>
        public event Action<double>? PeakAvailable;

        /// <summary>Fires once when sustained silence exceeds VadSilenceTimeout.</summary>
        public event Action? SilenceDetected;

        // VAD settings
        public bool      VadEnabled        { get; set; } = false;
        public double    VadThreshold      { get; set; } = 5.0;
        public TimeSpan  VadSilenceTimeout { get; set; } = TimeSpan.FromSeconds(1.8);
        public TimeSpan  VadGracePeriod    { get; set; } = TimeSpan.FromSeconds(1.0);

        public void StartRecording(string deviceId, string filePath)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(deviceId);

                _capture = new WasapiCapture(device, true, 50);
                _capture.WaveFormat = new WaveFormat(16000, 1);
                _writer  = new WaveFileWriter(filePath, _capture.WaveFormat);

                _stopTcs          = new TaskCompletionSource<bool>();
                _recordingStarted = DateTime.UtcNow;
                _lastSoundTime    = DateTime.UtcNow;
                _vadSilenceFired  = false;

                _capture.DataAvailable    += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;

                _capture.StartRecording();
                IsRecording = true;
            }
            catch (Exception ex)
            {
                IsRecording = false;
                _stopTcs?.TrySetResult(false);
                throw new InvalidOperationException(
                    $"Не удалось запустить запись: {ex.Message}", ex);
            }
        }

        public Task StopRecordingAsync()
        {
            if (_capture == null || _stopTcs == null) return Task.CompletedTask;
            IsRecording = false;
            VadEnabled  = false;
            _capture.StopRecording();
            return _stopTcs.Task;
        }

        public void StopRecording()
        {
            IsRecording = false;
            VadEnabled  = false;
            _capture?.StopRecording();
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs a)
        {
            if (a.BytesRecorded > 0 && _writer != null)
                _writer.Write(a.Buffer, 0, a.BytesRecorded);

            if (_capture == null) return;

            double peak = CalculatePeak(a.Buffer, a.BytesRecorded, _capture.WaveFormat);

            var now = DateTime.UtcNow;
            if ((now - _lastPeakTime).TotalMilliseconds >= PeakIntervalMs)
            {
                _lastPeakTime = now;
                PeakAvailable?.Invoke(peak);
            }

            // VAD logic
            if (!VadEnabled || !IsRecording) return;
            if ((now - _recordingStarted) < VadGracePeriod) return;

            if (peak > VadThreshold)
            {
                _lastSoundTime   = now;
                _vadSilenceFired = false;
            }
            else if (!_vadSilenceFired &&
                     (now - _lastSoundTime) >= VadSilenceTimeout)
            {
                _vadSilenceFired = true;
                SilenceDetected?.Invoke();
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs a)
        {
            _writer?.Dispose();  _writer  = null;
            _capture?.Dispose(); _capture = null;
            _stopTcs?.TrySetResult(true);
        }

        public static double CalculatePeak(byte[] buffer, int bytesRecorded, WaveFormat format)
        {
            float max = 0f;

            if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                for (int i = 0; i + 3 < bytesRecorded; i += 4)
                {
                    float s = Math.Abs(BitConverter.ToSingle(buffer, i));
                    if (s > max) max = s;
                }
            }
            else
            {
                for (int i = 0; i + 1 < bytesRecorded; i += 2)
                {
                    float s = Math.Abs(BitConverter.ToInt16(buffer, i) / 32768f);
                    if (s > max) max = s;
                }
            }

            return Math.Min(100.0, Math.Sqrt(max) * 100.0);
        }
    }
}
