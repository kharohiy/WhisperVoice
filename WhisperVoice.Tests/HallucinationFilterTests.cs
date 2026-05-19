using FluentAssertions;
using System;
using System.IO;
using System.Text.Json;
using WhisperVoice.Services;
using Xunit;

namespace WhisperVoice.Tests
{
    public class HallucinationFilterTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly HallucinationFilter _filter;

        public HallucinationFilterTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);

            // Create the test dictionary file (hallucinations.json)
            var dictionaryPath = Path.Combine(_tempDir, "hallucinations.json");
            var customPatterns = new[] { "amara.org", "watching", "dimatorzok" };
            File.WriteAllText(dictionaryPath, JsonSerializer.Serialize(customPatterns));

            _filter = new HallucinationFilter(_tempDir);
        }

        [Theory]
        [InlineData("Thank you for watching!")]
        [InlineData("Visit amara.org for more")]
        [InlineData("Субтитры создавал Dimatorzok")]
        [InlineData("x")] // Too short to pass
        public void Check_ReturnsFalse_ForHallucinations(string input)
        {
            bool isValid = _filter.Check(input, out string cleaned);
            
            isValid.Should().BeFalse();
            cleaned.Should().BeEmpty();
        }

        [Fact]
        public void Check_ReturnsTrue_ForValidTranscription()
        {
            string input = "This is a completely valid and normal transcription.";
            
            bool isValid = _filter.Check(input, out string cleaned);
            
            isValid.Should().BeTrue();
            cleaned.Should().Be(input);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
    }
}
