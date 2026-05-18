using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Threading.Tasks;
using WhisperVoice.Services; 

namespace WhisperVoice
{
    /// <summary>
    /// Captures microphone audio to a WAV file via WASAPI.
    /// Exposes a peak-level event for the VU-meter and built-in
    /// software Voice Activity Detection (VAD) for auto-stop.
    /// </summary>
    public class AudioRecorder : IAudioSource
    {
        private static readonly DiagnosticLogger Log = DiagnosticLogger.Instance;
        private const string Comp = "AudioRecorder";

        private WasapiCapture? _capture;
        private WaveFileWriter? _writer;
        private TaskCompletionSource<bool>? _stopTcs;

        private DateTime _lastPeakTime = DateTime.MinValue;
        private DateTime _lastTraceLogTime = DateTime.MinValue; // throttle TRACE logs
        private const int PeakIntervalMs = 40;
        private const int TraceLogIntervalMs = 5_000; // log VAD state every 5 s

        // VAD state
        private DateTime _recordingStarted = DateTime.MinValue;
        private DateTime _lastSoundTime = DateTime.MinValue;
        private bool _vadSilenceFired = false;

        public bool IsRecording { get; private set; }

        /// <summary>Fires ~25×/sec with 0-100 peak percentage.</summary>
        public event Action<double>? PeakAvailable;

        /// <summary>Fires once when sustained silence exceeds VadSilenceTimeout.</summary>
        public event Action? SilenceDetected;

        // ── BUG-1 FIX (WASAPI vector) ─────────────────────────────────────────
        public event Action<Exception>? RecordingAborted;
        // ─────────────────────────────────────────────────────────────────────

        // VAD settings
        public bool VadEnabled { get; set; } = false;
        public double VadThreshold { get; set; } = 5.0;
        public TimeSpan VadSilenceTimeout { get; set; } = TimeSpan.FromSeconds(1.8);
        public TimeSpan VadGracePeriod { get; set; } = TimeSpan.FromSeconds(1.5);

        public bool StartRecording(string deviceId, string filePath)
        {
            Log.Info(Comp, $"StartRecording called  deviceId={deviceId}  filePath={filePath}");

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(deviceId);

                Log.Info(Comp, $"Device resolved: FriendlyName=\"{device.FriendlyName}\"  " +
                               $"State={device.State}  ID={device.ID}");

                _capture = new WasapiCapture(device, true, 50);
                _capture.WaveFormat = new WaveFormat(16000, 1);

                Log.Info(Comp, $"WaveFormat set: SampleRate={_capture.WaveFormat.SampleRate}  " +
                               $"Channels={_capture.WaveFormat.Channels}  " +
                               $"Encoding={_capture.WaveFormat.Encoding}  " +
                               $"BitsPerSample={_capture.WaveFormat.BitsPerSample}");

                _writer = new WaveFileWriter(filePath, _capture.WaveFormat);

                _stopTcs = new TaskCompletionSource<bool>();
                _recordingStarted = DateTime.UtcNow;
                _lastSoundTime = DateTime.UtcNow;
                _vadSilenceFired = false;

                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;

                _capture.StartRecording();
                IsRecording = true;

                Log.Info(Comp, $"WASAPI capture STARTED successfully  " +
                               $"VadEnabled={VadEnabled}  VadThreshold={VadThreshold}  " +
                               $"VadSilenceTimeout={VadSilenceTimeout.TotalSeconds:F1}s  " +
                               $"VadGracePeriod={VadGracePeriod.TotalSeconds:F1}s");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(Comp, ex, "StartRecording FAILED — device unavailable or COM error");
                IsRecording = false;
                _stopTcs?.TrySetResult(false);
                _writer?.Dispose(); _writer = null;
                _capture?.Dispose(); _capture = null;
                return false;
            }
        }

        public Task StopRecordingAsync()
        {
            Log.Info(Comp, $"StopRecordingAsync called (intentional stop)  IsRecording={IsRecording}");

            if (_capture == null || _stopTcs == null)
            {
                Log.Warn(Comp, "StopRecordingAsync: _capture or _stopTcs is null — returning immediately");
                return Task.CompletedTask;
            }

            IsRecording = false;
            VadEnabled = false;
            _capture.StopRecording();
            return _stopTcs.Task;
        }

        public void StopRecording()
        {
            Log.Info(Comp, $"StopRecording (sync) called (intentional stop)  IsRecording={IsRecording}");
            IsRecording = false;
            VadEnabled = false;
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

            if (!VadEnabled || !IsRecording) return;
            if ((now - _recordingStarted) < VadGracePeriod)
            {
                _lastSoundTime = now;
                return;
            }

            if (peak > VadThreshold)
            {
                _lastSoundTime = now;
                _vadSilenceFired = false;
            }
            else if (!_vadSilenceFired &&
                     (now - _lastSoundTime) >= VadSilenceTimeout)
            {
                _vadSilenceFired = true;
                Log.Warn(Comp,
                    $"VAD SILENCE TRIGGERED  peak={peak:F2}  " +
                    $"silenceDuration={(now - _lastSoundTime).TotalSeconds:F2}s  " +
                    $"VadSilenceTimeout={VadSilenceTimeout.TotalSeconds:F1}s  " +
                    $"recordingDuration={(now - _recordingStarted).TotalSeconds:F1}s  " +
                    $"VadThreshold={VadThreshold}");
                SilenceDetected?.Invoke();
            }
        }

        // ── BUG-1 FIX (WASAPI vector) ─────────────────────────────────────────
        private void OnRecordingStopped(object? sender, StoppedEventArgs a)
        {
            var exception = a.Exception;
            bool wasExternalAbort = IsRecording && exception != null;

            // ── CRITICAL: log the full stop context before disposing state ───
            if (exception != null)
            {
                Log.Error(Comp,
                    $"OnRecordingStopped  EXCEPTION  " +
                    $"HRESULT=0x{exception.HResult:X8}  " +
                    $"Message={exception.Message}  " +
                    $"IsRecording={IsRecording}  " +
                    $"wasExternalAbort={wasExternalAbort}");
            }
            else
            {
                Log.Info(Comp,
                    $"OnRecordingStopped  CLEAN  " +
                    $"IsRecording={IsRecording}  " +
                    $"wasExternalAbort={wasExternalAbort}");
            }

            _writer?.Dispose(); _writer = null;
            _capture?.Dispose(); _capture = null;
            _stopTcs?.TrySetResult(true);

            if (wasExternalAbort)
            {
                IsRecording = false;
                Log.Error(Comp, $"WASAPI EXTERNAL ABORT confirmed — raising RecordingAborted event");
                RecordingAborted?.Invoke(exception!);
            }
        }
        // ─────────────────────────────────────────────────────────────────────

                public static double CalculatePeak(byte[] buffer, int bytesRecorded, WaveFormat format)
        {
            float max = 0f;
            float sum = 0f;
            int count = 0;

            if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                for (int i = 0; i + 3 < bytesRecorded; i += 4)
                {
                    sum += BitConverter.ToSingle(buffer, i);
                    count++;
                }
                float avg = count > 0 ? sum / count : 0f;

                for (int i = 0; i + 3 < bytesRecorded; i += 4)
                {
                    float s = Math.Abs(BitConverter.ToSingle(buffer, i) - avg);
                    if (s > max) max = s;
                }
            }
            else
            {
                for (int i = 0; i + 1 < bytesRecorded; i += 2)
                {
                    sum += (BitConverter.ToInt16(buffer, i) / 32768f);
                    count++;
                }
                float avg = count > 0 ? sum / count : 0f;

                for (int i = 0; i + 1 < bytesRecorded; i += 2)
                {
                    float s = Math.Abs((BitConverter.ToInt16(buffer, i) / 32768f) - avg);
                    if (s > max) max = s;
                }
            }

            return Math.Min(100.0, Math.Sqrt(max) * 100.0);
        }
        public void Dispose()
        {
            StopRecording();
            _writer?.Dispose();
            _capture?.Dispose();
        }
    }
}