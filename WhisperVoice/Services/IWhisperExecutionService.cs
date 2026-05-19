using System;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperVoice.Services
{
    public interface IWhisperExecutionService
    {
        Task<string?> RunAsync(
            string modelPath,
            string lang,
            bool isTranslate,
            string techPrompt,
            IProgress<string>? progress,
            Action<string>? logAction,
            CancellationToken token,
            int beamSize = 5,
            int bestOf = 5,
            double temperature = 0.0,
            double noSpeechThreshold = 0.6,
            Action<bool>? vulkanStatusCallback = null);
    }
}
