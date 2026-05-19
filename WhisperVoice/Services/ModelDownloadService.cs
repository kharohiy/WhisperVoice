using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperVoice.Services
{
    public interface IModelDownloadService
    {
        Task DownloadAsync(string url, string destinationPath, string expectedSha256,
            IProgress<double> progress, CancellationToken ct = default);
    }

    public sealed class ModelDownloadService : IModelDownloadService
    {
        private const int BufferSize = 81_920;
        private readonly HttpClient _http;
        public ModelDownloadService(HttpClient http) => _http = http;

        public async Task DownloadAsync(string url, string destinationPath, string expectedSha256,
            IProgress<double> progress, CancellationToken ct = default)
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var totalBytes = resp.Content.Headers.ContentLength;
            var tempPath = destinationPath + ".part";

            bool checkHash = !string.IsNullOrWhiteSpace(expectedSha256);
            using var hasher = checkHash 
                ? System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256) 
                : null;

            try
            {
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(tempPath, FileMode.Create,
                    FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

                var buf = new byte[BufferSize];
                long received = 0;
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    if (checkHash)
                    {
                        hasher!.AppendData(buf, 0, read);
                    }
                    received += read;
                    progress.Report(totalBytes > 0 ? (double)received / totalBytes.Value : -1);
                }
            }
            catch
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
                throw;
            }

            if (checkHash)
            {
                var hashBytes = hasher!.GetHashAndReset();
                var hashStr = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                if (!hashStr.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    var msg = $"SHA256 mismatch! Expected {expectedSha256}, got {hashStr}. Download corrupted or tampered.";
                    DiagnosticLogger.Instance.Error("ModelDownloadService", msg);
                    throw new System.Security.Cryptography.CryptographicException(msg);
                }
            }

            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(tempPath, destinationPath);
            progress.Report(1.0);
        }
    }
}
