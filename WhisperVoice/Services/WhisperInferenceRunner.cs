using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperVoice.Services
{
    public sealed class InferenceResult
    {
        public bool Success { get; }
        public string Text { get; }
        public string ErrorMessage { get; }
        public PipelineError ErrorType { get; }
        public bool IsHallucinationOrSilence { get; }

        public InferenceResult(bool success, string text = "", string errorMessage = "", PipelineError errorType = PipelineError.None, bool isHallucinationOrSilence = false)
        {
            Success = success;
            Text = text;
            ErrorMessage = errorMessage;
            ErrorType = errorType;
            IsHallucinationOrSilence = isHallucinationOrSilence;
        }
    }

    public interface IWhisperInferenceRunner
    {
        Task<InferenceResult> RunPipelineAsync(
            ProcessingMode mode,
            string targetLang,
            bool isTranslate,
            AppSettings settings,
            IProgress<string> progress,
            Action<string> onPhaseChanged,
            Action<VulkanStatus> onVulkanStatus,
            Func<string, string> getResource,
            CancellationToken token);
    }

    public sealed class WhisperInferenceRunner : IWhisperInferenceRunner
    {
        private readonly IWhisperExecutionService _whisper;
        private readonly HardwareCheckService _hardware;
        private readonly HallucinationFilter _hallucinationFilter;
        private readonly TextPostProcessorService _postProcessor;

        public WhisperInferenceRunner(
            IWhisperExecutionService whisper, 
            HardwareCheckService hardware, 
            HallucinationFilter hallucinationFilter, 
            TextPostProcessorService postProcessor)
        {
            _whisper = whisper;
            _hardware = hardware;
            _hallucinationFilter = hallucinationFilter;
            _postProcessor = postProcessor;
        }

        public async Task<InferenceResult> RunPipelineAsync(
            ProcessingMode mode,
            string targetLang,
            bool isTranslate,
            AppSettings settings,
            IProgress<string> progress,
            Action<string> onPhaseChanged,
            Action<VulkanStatus> onVulkanStatus,
            Func<string, string> getResource,
            CancellationToken token)
        {
            try
            {
                var (ramOk, ramMsg) = await _hardware.CheckRamAsync(getResource("ErrLowRam"));
                if (!ramOk)
                    return new InferenceResult(false, errorMessage: ramMsg, errorType: PipelineError.LowMemoryFallback);

                string model = settings.LastModelPath;
                if (string.IsNullOrEmpty(model) || !File.Exists(model))
                    return new InferenceResult(false, errorMessage: "ModelMissing", errorType: PipelineError.ModelMissing);

                onPhaseChanged?.Invoke("RunningInference");

                string? targetProfileId = mode switch
                {
                    ProcessingMode.Primary => settings.PrimaryProfileId,
                    ProcessingMode.Translate => settings.TranslateProfileId,
                    ProcessingMode.Prompt => settings.PromptProfileId,
                    _ => null
                };

                WhisperProfile? activeProfile = string.IsNullOrEmpty(targetProfileId) 
                    ? null : settings.CustomProfiles?.Find(p => p.Id == targetProfileId);

                string techPrompt = activeProfile != null 
                    ? activeProfile.PromptTags 
                    : (mode == ProcessingMode.Translate ? settings.PromptTranslate : 
                       mode == ProcessingMode.Prompt ? LoadDictPrompt() : string.Empty);

                double temperature = activeProfile != null ? activeProfile.Temperature : settings.Temperature;

                string? rawResult = await _whisper.RunAsync(
                    model, targetLang, isTranslate, techPrompt, progress,
                    msg => DiagnosticLogger.Instance.Info("WhisperInferenceRunner", msg),
                    token,
                    beamSize: settings.BeamSize,
                    bestOf: settings.BestOf,
                    temperature: temperature,
                    noSpeechThreshold: settings.NoSpeechThreshold,
                    vulkanStatusCallback: isVulkan =>
                    {
                        var status = isVulkan ? VulkanStatus.Active : VulkanStatus.CpuFallback;
                        _hardware.LastVulkanStatus = status;
                        onVulkanStatus?.Invoke(status);
                    });

                if (rawResult is null)
                    return new InferenceResult(false, errorMessage: "Inference returned null");

                onPhaseChanged?.Invoke("FilteringHallucinations");

                if (!_hallucinationFilter.Check(rawResult, out string cleanResult))
                    return new InferenceResult(true, isHallucinationOrSilence: true);

                string finalResult = _postProcessor.Process(cleanResult);
                if (string.IsNullOrWhiteSpace(finalResult) || !finalResult.Any(char.IsLetterOrDigit))
                    return new InferenceResult(true, isHallucinationOrSilence: true);

                return new InferenceResult(true, text: finalResult);
            }
            catch (OperationCanceledException)
            {
                return new InferenceResult(false, errorMessage: "RecordingAborted", errorType: PipelineError.RecordingAborted);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Instance.Error("WhisperInferenceRunner", ex, "Pipeline failed");
                return new InferenceResult(false, errorMessage: ex.Message, errorType: PipelineError.RecordingAborted);
            }
        }

        private static string LoadDictPrompt()
        {
            try
            {
                string dictPath = Path.Combine(AppSettings.AppDataDir, "dictionary", "dictionary.txt");
                if (!File.Exists(dictPath)) return string.Empty;
                string raw = File.ReadAllText(dictPath).Replace("\r\n", " ").Replace("\n", " ").Replace("\"", "");
                return raw.Length > 250 ? raw[..250] : raw;
            }
            catch { return string.Empty; }
        }
    }
}
