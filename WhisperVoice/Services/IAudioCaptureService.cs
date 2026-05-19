using System;
using System.Threading.Tasks;

namespace WhisperVoice.Services
{
    public interface IAudioCaptureService
    {
        bool IsRecording { get; }
        bool StartRecording(string micId, string outputPath, double vadThreshold, double vadSilenceSeconds, bool vadEnabled = true);
        Task StopRecordingAsync();
    }
}
