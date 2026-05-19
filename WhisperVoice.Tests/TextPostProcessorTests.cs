using FluentAssertions;
using WhisperVoice.Services;
using Xunit;

namespace WhisperVoice.Tests
{
    public class TextPostProcessorTests
    {
        private readonly TextPostProcessorService _processor = new();

        [Theory]
        [InlineData("[00:00:00.000 --> 00:00:05.000] hello world", "Hello world")]
        [InlineData("  [00:00:00.000 --> 00:00:05.000]   spaces   left  behind  ", "Spaces left behind")]
        public void Process_RemovesTimestamps_AndCleansBoundaries(string input, string expected)
        {
            var result = _processor.Process(input);
            result.Should().Be(expected);
        }

        [Theory]
        [InlineData("[music] Test", "Test")]
        [InlineData("Yes (coughs)", "Yes")]
        [InlineData("*phone rings*   Hello", "Hello")]
        [InlineData("  (coughs)  [music]  multiple  tags  ", "Multiple tags")]
        public void Process_RemovesAcousticHallucinations_AndCollapsesSpaces(string input, string expected)
        {
            var result = _processor.Process(input);
            result.Should().Be(expected);
        }
    }
}
