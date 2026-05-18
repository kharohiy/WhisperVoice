using System;
using System.Threading.Tasks;

namespace WhisperVoice.Services
{
    public interface IAudioSource : IDisposable
    {
        bool IsRecording { get; }
        event Action<double>? PeakAvailable;
        event Action? SilenceDetected;
        event Action<Exception>? RecordingAborted;
        
        double VadThreshold { get; set; }
        TimeSpan VadSilenceTimeout { get; set; }
        bool VadEnabled { get; set; }
        
        bool StartRecording(string deviceId, string outputPath);
        Task StopRecordingAsync();
        void StopRecording();
    }
}