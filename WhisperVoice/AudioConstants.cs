namespace WhisperVoice
{
    /// <summary>
    /// Centralized audio processing constants.
    /// All thresholds, limits, and timing values live here —
    /// no magic numbers scattered across services.
    /// </summary>
    internal static class AudioConstants
    {
        // ── WAV file structure ────────────────────────────────────────────────
        /// <summary>Standard WAV header size in bytes (RIFF + fmt + data chunks).</summary>
        public const int WavHeaderBytes = 44;

        // ── Silence / PCM energy filter ───────────────────────────────────────
        /// <summary>Minimum PCM sample count to consider audio worth processing.</summary>
        public const int MinSampleCount = 6400;

        /// <summary>Samples to skip at start and end of recording (warmup/cooldown noise).</summary>
        public const int EdgeSampleSkip = 4800;

        /// <summary>Minimum RMS energy threshold for microphone recordings.</summary>
        public const double RmsThresholdMic = 300.0;

        /// <summary>
        /// Minimum RMS energy threshold for loopback recordings.
        /// Lower than mic because background audio can be quiet by design.
        /// </summary>
        public const double RmsThresholdLoopback = 80.0;

        // ── Voice Activity Detection (VAD) defaults ───────────────────────────
        /// <summary>Peak percentage (0–100) below which mic is considered silent.</summary>
        public const double DefaultVadThreshold = 5.0;

        /// <summary>Continuous silence duration in seconds that triggers auto-stop.</summary>
        public const double DefaultVadSilenceSeconds = 1.8;

        /// <summary>
        /// Extra silence seconds added for loopback captures.
        /// Background audio needs a longer tail before auto-stopping.
        /// </summary>
        public const double LoopbackVadSilenceExtra = 3.0;

        /// <summary>Grace period after recording start before VAD is active (seconds).</summary>
        public const double DefaultVadGracePeriod = 1.5;

        // ── Peak meter ────────────────────────────────────────────────────────
        /// <summary>Minimum interval between PeakAvailable events (milliseconds).</summary>
        public const int PeakIntervalMs = 40;

        /// <summary>Interval for throttled TRACE-level VAD log output (milliseconds).</summary>
        public const int TraceLogIntervalMs = 5_000;
    }
}
