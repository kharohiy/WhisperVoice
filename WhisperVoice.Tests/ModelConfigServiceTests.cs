using FluentAssertions;
using Moq;
using Moq.Protected;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WhisperVoice.Models;
using WhisperVoice.Services;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WhisperVoice.Tests
{
    public class ModelConfigServiceTests
    {
        [Fact]
        public async Task GetModelConfigAsync_TrustedDomain_ReturnsRemote()
        {
            // Arrange
            var handlerMock = new Mock<HttpMessageHandler>();
            var response = new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"models\": [{\"name\": \"RemoteModel\", \"sha256\": \"123\"}]}")
            };

            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            var httpClient = new HttpClient(handlerMock.Object);
            var service = new ModelConfigService(httpClient);

            // Act
            var result = await service.GetModelConfigAsync("https://huggingface.co/config.json");

            // Assert
            result.Should().NotBeNull();
            result.Models.Should().NotBeNullOrEmpty();
            result.Models[0].Name.Should().Be("RemoteModel");
            result.Models[0].Sha256.Should().Be("123");
        }

        [Fact]
        public async Task GetModelConfigAsync_UntrustedDomain_BlockedWithoutHttpCall()
        {
            // Arrange
            var handlerMock = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(handlerMock.Object);
            var service = new ModelConfigService(httpClient);

            // Act
            var result = await service.GetModelConfigAsync("https://untrusted-site.com/config.json");

            // Assert
            handlerMock.Protected().Verify("SendAsync", Times.Never(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
            result.Should().NotBeNull();
            result.Models.Should().NotBeNullOrEmpty(); // From fallback default_models.json
            result.Models.Any(m => m.Name == "RemoteModel").Should().BeFalse();
        }

        [Fact]
        public async Task GetModelConfigAsync_HttpError_ReturnsSafeFallback()
        {
            // Arrange
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage { StatusCode = HttpStatusCode.NotFound });

            var httpClient = new HttpClient(handlerMock.Object);
            var service = new ModelConfigService(httpClient);

            // Act
            var result = await service.GetModelConfigAsync("https://huggingface.co/config.json");

            // Assert
            result.Should().NotBeNull();
            result.Models.Should().NotBeNullOrEmpty(); // From fallback
        }

        [Fact]
        public async Task GetModelConfigAsync_DynamicWhitelistedDomain_ReturnsRemote()
        {
            // Arrange
            var originalSettings = AppSettings.Load();
            var backupDomains = originalSettings.WhitelistedDomains;

            try
            {
                var settings = AppSettings.Load();
                settings.WhitelistedDomains = new[] { "dynamic-custom.net" };
                settings.Save();

                var handlerMock = new Mock<HttpMessageHandler>();
                var response = new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("{\"models\": [{\"name\": \"DynamicModel\", \"sha256\": \"abc\"}]}")
                };

                handlerMock.Protected()
                    .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                    .ReturnsAsync(response);

                var httpClient = new HttpClient(handlerMock.Object);
                var service = new ModelConfigService(httpClient);

                // Act
                var result = await service.GetModelConfigAsync("https://dynamic-custom.net/config.json");

                // Assert
                result.Should().NotBeNull();
                result.Models.Should().NotBeNullOrEmpty();
                result.Models[0].Name.Should().Be("DynamicModel");
                result.Models[0].Sha256.Should().Be("abc");
            }
            finally
            {
                var settings = AppSettings.Load();
                settings.WhitelistedDomains = backupDomains;
                settings.Save();
            }
        }

        [Fact]
        public async Task GetModelConfigAsync_RemovedFromWhitelistDomain_Blocked()
        {
            // Arrange
            var originalSettings = AppSettings.Load();
            var backupDomains = originalSettings.WhitelistedDomains;

            try
            {
                var settings = AppSettings.Load();
                settings.WhitelistedDomains = new[] { "some-other-allowed.com" }; // Huggingface is removed
                settings.Save();

                var handlerMock = new Mock<HttpMessageHandler>();
                var httpClient = new HttpClient(handlerMock.Object);
                var service = new ModelConfigService(httpClient);

                // Act
                var result = await service.GetModelConfigAsync("https://huggingface.co/config.json");

                // Assert
                handlerMock.Protected().Verify("SendAsync", Times.Never(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
                result.Should().NotBeNull();
                result.Models.Should().NotBeNullOrEmpty(); // Safe fallback default_models.json
                result.Models.Any(m => m.Name == "DynamicModel").Should().BeFalse();
            }
            finally
            {
                var settings = AppSettings.Load();
                settings.WhitelistedDomains = backupDomains;
                settings.Save();
            }
        }
    }
}
