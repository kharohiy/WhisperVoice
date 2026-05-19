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
    }
}
