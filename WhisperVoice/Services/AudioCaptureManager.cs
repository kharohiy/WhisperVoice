using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperVoice.Services
{
    public interface IAudioCaptureManager
    {
        IAudioCaptureService ActiveCapture { get; }
        bool StartRecording(AudioSource source, AppSettings settings, string tempWavPath, out PipelineError error, out string errorMessage);
        Task StopRecordingAsync();
        bool IsAudioWorthProcessing(string path);
    }

    public sealed class AudioCaptureManager : IAudioCaptureManager
    {
        private readonly IAudioCaptureService _micCapture;
        private readonly IAudioCaptureService _loopbackCapture;
        private IAudioCaptureService _activeCapture;

        public IAudioCaptureService ActiveCapture => _activeCapture;

        public AudioCaptureManager(IAudioCaptureService micCapture, IAudioCaptureService loopbackCapture)
        {
            _micCapture = micCapture;
            _loopbackCapture = loopbackCapture;
            _activeCapture = _micCapture;
        }

        public bool StartRecording(AudioSource source, AppSettings settings, string tempWavPath, out PipelineError error, out string errorMessage)
        {
            error = PipelineError.None;
            errorMessage = "";

            if (string.IsNullOrEmpty(settings.MicId) && source == AudioSource.Microphone)
            {
                error = PipelineError.MicDisconnected;
                errorMessage = "ErrMicUnplugged";
                return false;
            }

            try { if (File.Exists(tempWavPath)) File.Delete(tempWavPath); } catch { }
            
            _activeCapture = source == AudioSource.Loopback ? _loopbackCapture : _micCapture;

            double silenceTimeout = source == AudioSource.Loopback ? settings.VadSilenceSeconds + 3.0 : settings.VadSilenceSeconds;
            bool enableVad = !settings.IsPushToTalkEnabled;

            bool started = _activeCapture.StartRecording(settings.MicId, tempWavPath, settings.VadThreshold, silenceTimeout, enableVad);
            if (!started)
            {
                error = PipelineError.MicDisconnected;
                errorMessage = "ErrMicUnplugged";
                return false;
            }

            return true;
        }

        public Task StopRecordingAsync() => _activeCapture.StopRecordingAsync();

        public bool IsAudioWorthProcessing(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= AudioConstants.WavHeaderBytes) return false;

                byte[]? bytes = null;
                for (int i = 0; i < 3; i++)
                {
                    try { bytes = File.ReadAllBytes(path); break; }
                    catch (Exception ex) { DiagnosticLogger.Instance.Trace("AudioCaptureManager", $"Read WAV retry: {ex.Message}"); Thread.Sleep(50); }
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

                double sumSq = 0;
                for (int i = startSample; i < endSample; i++)
                {
                    double s = BitConverter.ToInt16(bytes, AudioConstants.WavHeaderBytes + i * 2) - dcOffset;
                    sumSq += s * s;
                }
                double rms = Math.Sqrt(sumSq / validCount);

                DiagnosticLogger.Instance.Info("AudioCaptureManager", $"[PCM Filter] RMS: {rms:F1}");

                double threshold = _activeCapture == _loopbackCapture
                    ? AudioConstants.RmsThresholdLoopback
                    : AudioConstants.RmsThresholdMic;

                return rms > threshold;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Instance.Warn("AudioCaptureManager", $"[PCM Filter] File error: {ex.Message} -> Fallback to True");
                return true;
            }
        }
    }
}
