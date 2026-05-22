using FluentAssertions;
using Moq;
using System.IO;
using System.Threading.Tasks;
using WhisperVoice.Services;
using Xunit;

namespace WhisperVoice.Tests
{
    /// <summary>
    /// Tests for RecordingOrchestrationService state-machine guarantees.
    /// Specifically: state MUST return to Idle after any failure in StartRecording.
    /// </summary>
    public class OrchestratorStateGuardTests
    {
        private readonly Mock<IAudioCaptureService> _micMock = new();
        private readonly Mock<IAudioCaptureService> _loopbackMock = new();
        private readonly Mock<IWhisperExecutionService> _whisperMock = new();
        private readonly RecordingOrchestrationService _orchestrator;

        public OrchestratorStateGuardTests()
        {
            var dummyModelPath = Path.Combine(Path.GetTempPath(), "dummy_state_guard.bin");
            File.WriteAllText(dummyModelPath, "dummy");

            var settings = AppSettings.Load();
            settings.MicId = "MockMic";
            settings.LastModelPath = dummyModelPath;
            settings.Save();

            var hardware = new HardwareCheckService();
            var filter = new HallucinationFilter(Path.GetTempPath());
            var postProcessor = new TextPostProcessorService();

            _orchestrator = new RecordingOrchestrationService(
                _micMock.Object, _loopbackMock.Object, _whisperMock.Object,
                hardware, filter, postProcessor, "temp_guard_test.wav");
        }

        // ── Проблема: если StartRecording внутри бросает исключение до TransitionTo,
        //    state остаётся Recording навсегда (Idle так и не наступает).
        //    fix/orchestrator-state-finally: try/finally гарантирует возврат в Idle.

        [Fact]
        public void StartRecording_WhenCaptureFails_StateReturnsToIdle()
        {
            // Arrange — мок бросает исключение при старте
            _micMock.Setup(m => m.StartRecording(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<double>(), It.IsAny<double>(), It.IsAny<bool>()))
                .Throws(new System.InvalidOperationException("Simulated device failure"));

            // Precondition
            _orchestrator.CurrentState.Should().Be(PipelineLifecycleState.Idle);

            // Act
            _orchestrator.StartRecording(new RecordingRequest(ProcessingMode.Primary, AudioSource.Microphone));

            // Assert — state обязан вернуться в Idle, не застрять в Recording
            _orchestrator.CurrentState.Should().Be(PipelineLifecycleState.Idle,
                because: "StartRecording must always return state to Idle on any failure");
        }

        [Fact]
        public void StartRecording_WhenAudioStartReturnsFalse_StateReturnsToIdle()
        {
            // Arrange — StartRecording возвращает false (устройство недоступно)
            _micMock.Setup(m => m.StartRecording(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<double>(), It.IsAny<double>(), It.IsAny<bool>()))
                .Returns(false);

            _orchestrator.CurrentState.Should().Be(PipelineLifecycleState.Idle);

            // Act
            _orchestrator.StartRecording(new RecordingRequest(ProcessingMode.Primary, AudioSource.Microphone));

            // Assert
            _orchestrator.CurrentState.Should().Be(PipelineLifecycleState.Idle,
                because: "Failed audio start must reset state back to Idle");
        }

        [Fact]
        public void StartRecording_WhenCalledTwiceConcurrently_OnlyOneSucceeds()
        {
            // Arrange — первый старт занимает время
            _micMock.Setup(m => m.StartRecording(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<double>(), It.IsAny<double>(), It.IsAny<bool>()))
                .Returns(true);

            // Act — два параллельных вызова
            System.Threading.Tasks.Parallel.For(0, 5, _ =>
                _orchestrator.StartRecording(new RecordingRequest(ProcessingMode.Primary, AudioSource.Microphone)));

            // Assert — StartRecording должен вызваться ровно один раз (state lock guard)
            _micMock.Verify(m => m.StartRecording(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<double>(), It.IsAny<double>(), It.IsAny<bool>()), Times.Once(),
                "Concurrent starts must be deduplicated by the state lock");
        }

        [Fact]
        public void Orchestrator_InitialState_IsIdle()
        {
            // Базовый инвариант — свежий orchestrator всегда Idle
            _orchestrator.CurrentState.Should().Be(PipelineLifecycleState.Idle);
            _orchestrator.IsRecording.Should().BeFalse();
            _orchestrator.IsProcessing.Should().BeFalse();
        }
    }
}
