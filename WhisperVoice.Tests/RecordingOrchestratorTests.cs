using FluentAssertions;
using Moq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WhisperVoice.Services;
using Xunit;

namespace WhisperVoice.Tests
{
    public class RecordingOrchestratorTests
    {
        private readonly Mock<IAudioCaptureService> _micMock = new();
        private readonly Mock<IAudioCaptureService> _loopbackMock = new();
        private readonly Mock<IWhisperExecutionService> _whisperMock = new();
        private readonly RecordingOrchestrationService _orchestrator;

        public RecordingOrchestratorTests()
        {
            var dummyModelPath = Path.Combine(Path.GetTempPath(), "dummy_model.bin");
            File.WriteAllText(dummyModelPath, "dummy");
            var settings = AppSettings.Load();
            settings.MicId = "MockMic";
            settings.LastModelPath = dummyModelPath;
            settings.Save();

            var hardware = new HardwareCheckService();
            var filter = new HallucinationFilter(Path.GetTempPath());
            var postProcessor = new TextPostProcessorService();
            _orchestrator = new RecordingOrchestrationService(_micMock.Object, _loopbackMock.Object, _whisperMock.Object, hardware, filter, postProcessor, "temp.wav");
        }

        [Fact]
        public async Task ToggleMode_SequentialEvents_StartThenStop()
        {
            // Arrange
            _micMock.Setup(m => m.StartRecording(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<bool>())).Returns(true);
            _micMock.SetupSequence(m => m.IsRecording).Returns(false).Returns(true);

            var settings = AppSettings.Load();

            // Act 1: Trigger Toggle (Start)
            await _orchestrator.HandleHotkeyTrigger(ProcessingMode.Primary, AudioSource.Microphone, isPushToTalk: false, isKeyDown: true, settings, key => key);

            // Assert 1
            _micMock.Verify(m => m.StartRecording(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<bool>()), Times.Once());

            // Act 2: Trigger Toggle again (Stop)
            await _orchestrator.HandleHotkeyTrigger(ProcessingMode.Primary, AudioSource.Microphone, isPushToTalk: false, isKeyDown: true, settings, key => key);

            // Assert 2
            _micMock.Verify(m => m.StopRecordingAsync(), Times.Once());
        }

        [Fact]
        public async Task PushToTalkMode_KeyDownStart_KeyUpStop()
        {
            // Arrange
            _micMock.Setup(m => m.StartRecording(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<bool>())).Returns(true);
            _micMock.SetupSequence(m => m.IsRecording).Returns(false).Returns(true);

            var settings = new AppSettings();

            // Act 1: Key Down (Start)
            await _orchestrator.HandleHotkeyTrigger(ProcessingMode.Primary, AudioSource.Microphone, isPushToTalk: true, isKeyDown: true, settings, key => key);
            
            // Assert 1
            _micMock.Verify(m => m.StartRecording(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<bool>()), Times.Once());

            // Act 2: Key Up (Stop)
            await _orchestrator.HandleHotkeyTrigger(ProcessingMode.Primary, AudioSource.Microphone, isPushToTalk: true, isKeyDown: false, settings, key => key);

            // Assert 2
            _micMock.Verify(m => m.StopRecordingAsync(), Times.Once());
        }

        [Fact]
        public void RapidStartTriggers_EarlyLock_DiscardsDuplicate()
        {
            // Arrange
            _micMock.Setup(m => m.StartRecording(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<bool>()))
                .Returns(() => { Thread.Sleep(50); return true; }); // Add artificial delay to ensure thread overlap

            // Act: Fire multiple concurrent requests
            Parallel.For(0, 10, i => 
            {
                _orchestrator.StartRecording(new RecordingRequest(ProcessingMode.Primary, AudioSource.Microphone));
            });

            // Assert: Inner start method should only be hit exactly once
            _micMock.Verify(m => m.StartRecording(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<bool>()), Times.Once());
        }
    }
}
