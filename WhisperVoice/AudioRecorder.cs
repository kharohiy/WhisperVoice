using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Threading.Tasks;

namespace WhisperVoice
{
    public class AudioRecorder
    {
        private WasapiCapture? capture;
        private WaveFileWriter? writer;
        private TaskCompletionSource<bool>? _stopTcs;

        private DateTime _lastPeakTime = DateTime.MinValue;
        private const int PeakIntervalMs = 40; // Обновляем чаще для плавности

        public bool IsRecording { get; private set; }
        public event Action<double>? PeakAvailable;

        public void StartRecording(string deviceId, string filePath)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(deviceId);

                // Захват с буфером 50мс для плавного индикатора
                capture = new WasapiCapture(device, true, 50);
                capture.WaveFormat = new WaveFormat(16000, 1);
                writer = new WaveFileWriter(filePath, capture.WaveFormat);

                _stopTcs = new TaskCompletionSource<bool>();

                capture.DataAvailable += (s, a) =>
                {
                    if (a.BytesRecorded > 0 && writer != null)
                        writer.Write(a.Buffer, 0, a.BytesRecorded);

                    if (capture != null)
                        RaisePeakIfDue(a.Buffer, a.BytesRecorded, capture.WaveFormat);
                };

                capture.RecordingStopped += (s, a) =>
                {
                    writer?.Dispose(); writer = null;
                    capture?.Dispose(); capture = null;
                    _stopTcs?.TrySetResult(true);
                };

                capture.StartRecording();
                IsRecording = true;
            }
            catch (Exception ex)
            {
                IsRecording = false;
                _stopTcs?.TrySetResult(false);
                throw new InvalidOperationException($"Не удалось запустить запись: {ex.Message}", ex);
            }
        }

        public Task StopRecordingAsync()
        {
            if (capture == null || _stopTcs == null) return Task.CompletedTask;
            IsRecording = false;
            capture.StopRecording();
            return _stopTcs.Task;
        }

        public void StopRecording()
        {
            IsRecording = false;
            capture?.StopRecording();
        }

        private void RaisePeakIfDue(byte[] buffer, int bytesRecorded, WaveFormat format)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastPeakTime).TotalMilliseconds < PeakIntervalMs) return;
            _lastPeakTime = now;
            PeakAvailable?.Invoke(CalculatePeak(buffer, bytesRecorded, format));
        }

        public static double CalculatePeak(byte[] buffer, int bytesRecorded, WaveFormat format)
        {
            float max = 0;

            if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                // Правильное чтение 32-bit Float
                for (int i = 0; i < bytesRecorded; i += 4)
                {
                    float sample = Math.Abs(BitConverter.ToSingle(buffer, i));
                    if (sample > max) max = sample;
                }
            }
            else
            {
                // Чтение 16-bit PCM (на всякий случай)
                for (int i = 0; i < bytesRecorded - 1; i += 2)
                {
                    short sample = BitConverter.ToInt16(buffer, i);
                    float floatSample = Math.Abs(sample / 32768f);
                    if (floatSample > max) max = floatSample;
                }
            }

            return Math.Min(100, Math.Sqrt(max) * 100);
        }
    }
}