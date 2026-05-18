using System;
using System.Runtime;
using System.Threading.Tasks;

namespace WhisperVoice.Services
{
    public enum VulkanStatus
    {
        Unknown,
        Active,
        CpuFallback
    }

    public class HardwareCheckService
    {
        public int MinRamMb { get; set; } = 400;
        public VulkanStatus LastVulkanStatus { get; set; } = VulkanStatus.Unknown;

        public Task<(bool Ok, string Message)> CheckRamAsync(string errorFormat = "Not enough RAM (need >= {0} MB free).")
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
    }
}