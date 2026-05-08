using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WhisperVoice.Models;

namespace WhisperVoice.Services
{
    public interface IModelConfigService
    {
        Task<ModelConfig> GetModelConfigAsync(string remoteUrl, CancellationToken ct = default);
    }

    public sealed class ModelConfigService : IModelConfigService
    {
        private readonly HttpClient _http;
        public ModelConfigService(HttpClient http) => _http = http;

        public async Task<ModelConfig> GetModelConfigAsync(string remoteUrl, CancellationToken ct = default)
        {
            // Attempt 1 — remote URL
            try
            {
                using var resp = await _http.GetAsync(remoteUrl, ct);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var cfg = await JsonSerializer.DeserializeAsync<ModelConfig>(stream, cancellationToken: ct);
                return cfg ?? throw new InvalidDataException("Remote config was null.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* fall through */ }

            // Attempt 2 — embedded resource Resources/default_models.json
            const string resName = "WhisperVoice.Resources.default_models.json";
            using var resStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName)
                ?? throw new FileNotFoundException($"Embedded resource '{resName}' not found.");
            var fallback = JsonSerializer.Deserialize<ModelConfig>(resStream);
            return fallback ?? throw new InvalidDataException("Embedded config was null.");
        }
    }
}
