using Xunit;
using WhisperVoice.Services;

namespace WhisperVoice.Tests
{
    public class TextPostProcessorServiceTests
    {
        private readonly TextPostProcessorService _processor = new();

        [Fact]
        public void Process_StripsSrtTimestampLines()
        {
            // SRT format: [00:00:00.000 --> 00:00:02.500]
            string input = "[00:00:00.000 --> 00:00:02.500] Hello world";
            string result = _processor.Process(input);
            Assert.Equal("Hello world", result);
        }

        [Fact]
        public void Process_StripsVttTimestampLines()
        {
            // VTT format: (00:00:00.000 --> 00:00:02.500)
            string input = "(00:00:00.000 --> 00:00:02.500) This is a test";
            string result = _processor.Process(input);
            Assert.Equal("This is a test", result);
        }

        [Fact]
        public void Process_StripsMultipleTimestamps()
        {
            string input = "[00:00:00.000 --> 00:00:02.000] First line\n" +
                          "[00:00:02.000 --> 00:00:04.000] Second line";
            string result = _processor.Process(input);
            Assert.Equal("First line\nSecond line", result);
        }

        [Fact]
        public void Process_CollapsesDoubleSpaces()
        {
            string input = "Hello  world   test";
            string result = _processor.Process(input);
            Assert.Equal("Hello world test", result);
        }

        [Fact]
        public void Process_CapitalizesFirstLetter()
        {
            string input = "hello world";
            string result = _processor.Process(input);
            Assert.Equal("Hello world", result);
        }

        [Fact]
        public void Process_DoesNotDoubleCapitalize()
        {
            string input = "Hello world";
            string result = _processor.Process(input);
            Assert.Equal("Hello world", result);
        }

        [Fact]
        public void Process_CombinedTransformations()
        {
            // Real-world case: timestamp + double spaces + lowercase start
            string input = "[00:00:00.100 --> 00:00:01.500] the quick  brown  fox";
            string result = _processor.Process(input);
            Assert.Equal("The quick brown fox", result);
        }

        [Fact]
        public void Process_TrimsWhitespace()
        {
            string input = "  hello world  \n";
            string result = _processor.Process(input);
            Assert.Equal("Hello world", result);
        }

        [Fact]
        public void Process_HandlesEmptyString()
        {
            string result = _processor.Process("");
            Assert.Equal("", result);
        }

        [Fact]
        public void Process_HandlesOnlyWhitespace()
        {
            string result = _processor.Process("   \n\t  ");
            Assert.Equal("", result);
        }

        [Fact]
        public void Process_ComplexRealWorldExample()
        {
            // Whisper output with leaked subtitles + spacing issues
            string input = @"[00:00:00.000 --> 00:00:02.000] спасибо за просмотр
привет  мир  это  тест
[00:00:02.000 --> 00:00:04.000] и еще один";

            string result = _processor.Process(input);
            
            // Expects: timestamps stripped, spaces collapsed, first letter capitalized
            string expected = "Привет мир это тест\nи еще один";
            Assert.Equal(expected, result);
        }
    }
}
