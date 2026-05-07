using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Threading.Tasks;

namespace WhisperVoice.Services
{
    /// <summary>
    /// Performs local hardware availability checks before heavy Whisper operations.
    /// Extracted from MainWindow to honour SRP.
    /// Error message format strings are injected by the caller so this service
    /// stays free of any UI / localisation concerns.
    /// </summary>
    public class HardwareCheckService
    {
        /// <summary>Minimum free VRAM in MB before showing a warning.</summary>
        public int MinVramMb { get; set; } = 1000;

        /// <summary>Minimum contiguous RAM block required (MB). Passed to MemoryFailPoint.</summary>
        public int MinRamMb { get; set; } = 400;

        // ── RAM ────────────────────────────────────────────────────────────
        /// <param name="errorFormat">Format string with one placeholder {0} for MinRamMb.</param>
        public Task<(bool Ok, string Message)> CheckRamAsync(string errorFormat = "Not enough RAM (need ≥ {0} MB free).")
        {
            return Task.Run(() =>
            {
                try
                {
                    using var _ = new MemoryFailPoint(MinRamMb);
                    return (true, string.Empty);
                }
                catch (InsufficientMemoryException)
                {
                    return (false, string.Format(errorFormat, MinRamMb));
                }
            });
        }

        // ── VRAM ───────────────────────────────────────────────────────────
        /// <param name="errorFormat">Format string with two placeholders: {0} = free MB, {1} = MinVramMb.</param>
        public async Task<(bool Ok, string Message)> CheckVramAsync(string errorFormat = "VRAM almost full ({0} MB free, need ≥ {1} MB).")
        {
            try
            {
                var psi = new ProcessStartInfo(
                    "nvidia-smi",
                    "--query-gpu=memory.free --format=csv,noheader,nounits")
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true
                };

                using var p = Process.Start(psi);
                if (p == null) return (true, string.Empty);   // no nvidia-smi — skip

                string raw = await p.StandardOutput
                    .ReadToEndAsync()
                    .WaitAsync(TimeSpan.FromSeconds(4));

                long minFree = raw
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => long.TryParse(l.Trim(), out long v) ? v : long.MaxValue)
                    .Min();

                if (minFree < MinVramMb)
                    return (false, string.Format(errorFormat, minFree, MinVramMb));

                return (true, string.Empty);
            }
            catch
            {
                // nvidia-smi not installed or GPU not available — not an error
                return (true, string.Empty);
            }
        }
    }
}
