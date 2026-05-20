using System;

namespace WhisperVoice.Services
{
    public enum PipelineLifecycleState
    {
        Idle,
        Recording,
        ProcessingAudio,
        RunningInference,
        FilteringHallucinations,
        Completed,
        Failed
    }

    public enum PipelineError
    {
        None,
        MicDisconnected,
        ModelMissing,
        LowMemoryFallback,
        RecordingAborted
    }

    public sealed class PipelineStatusReport
    {
        public PipelineLifecycleState State { get; }
        public double ProgressPercentage { get; } // 0.0 to 100.0, -1.0 for indeterminate
        public string Message { get; }
        public DiagnosticLogger.Level LogLevel { get; }
        public PipelineError Error { get; }

        public PipelineStatusReport(
            PipelineLifecycleState state, 
            double progressPercentage, 
            string message, 
            DiagnosticLogger.Level logLevel = DiagnosticLogger.Level.INFO,
            PipelineError error = PipelineError.None)
        {
            State = state;
            ProgressPercentage = progressPercentage;
            Message = message;
            LogLevel = logLevel;
            Error = error;
        }
    }
}
