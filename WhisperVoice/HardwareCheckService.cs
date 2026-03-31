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
    /// </summary>
    public class HardwareCheckService
    {
        /// <summary>Minimum free VRAM in MB before showing a warning.</summary>
        public int MinVramMb { get; set; } = 1000;

        /// <summary>Minimum contiguous RAM block required (MB). Passed to MemoryFailPoint.</summary>
        public int MinRamMb { get; set; } = 400;

        // ── RAM ────────────────────────────────────────────────────────────
        public Task<(bool Ok, string Message)> CheckRamAsync()
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
                    return (false, $"Недостаточно оперативной памяти (нужно ≥ {MinRamMb} МБ свободно).");
                }
            });
        }

        // ── VRAM ───────────────────────────────────────────────────────────
        public async Task<(bool Ok, string Message)> CheckVramAsync()
        {
            try
            {
                var psi = new ProcessStartInfo(
                    "nvidia-smi",
                    "--query-gpu=memory.free --format=csv,noheader,nounits")
                {
                    UseShellExecute      = false,
                    CreateNoWindow       = true,
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
                    return (false, $"VRAM почти заполнена ({minFree} МБ свободно, нужно ≥ {MinVramMb} МБ).");

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
