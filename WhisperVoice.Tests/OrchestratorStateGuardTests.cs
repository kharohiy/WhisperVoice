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

        // ── Issue: if StartRecording throws an exception before TransitionTo,
        //    the state remains Recording forever (Idle is never reached).
        //    fix/orchestrator-state-finally: try/finally guarantees return to Idle.

        [Fact]
        public void StartRecording_WhenCaptureFails_StateReturnsToIdle()
        {
            // Arrange — mock throws exception on start
            _micMock.Setup(m => m.StartRecording(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<double>(), It.IsAny<double>(), It.IsAny<bool>()))
                .Throws(new System.InvalidOperationException("Simulated device failure"));

            // Precondition
            _orchestrator.CurrentState.Should().Be(PipelineLifecycleState.Idle);

            // Act
            _orchestrator.StartRecording(new RecordingRequest(ProcessingMode.Primary, AudioSource.Microphone));

            // Assert — state must return to Idle, must not be stuck in Recording
            _orchestrator.CurrentState.Should().Be(PipelineLifecycleState.Idle,
                because: "StartRecording must always return state to Idle on any failure");
        }

        [Fact]
        public void StartRecording_WhenAudioStartReturnsFalse_StateReturnsToIdle()
        {
            // Arrange — StartRecording returns false (device unavailable)
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
            // Arrange — first start takes time
            _micMock.Setup(m => m.StartRecording(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<double>(), It.IsAny<double>(), It.IsAny<bool>()))
                .Returns(true);

            // Act — two parallel calls
            System.Threading.Tasks.Parallel.For(0, 5, _ =>
                _orchestrator.StartRecording(new RecordingRequest(ProcessingMode.Primary, AudioSource.Microphone)));

            // Assert — StartRecording must be called exactly once (state lock guard)
            _micMock.Verify(m => m.StartRecording(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<double>(), It.IsAny<double>(), It.IsAny<bool>()), Times.Once(),
                "Concurrent starts must be deduplicated by the state lock");
        }

        [Fact]
        public void Orchestrator_InitialState_IsIdle()
        {
            // Basic invariant — fresh orchestrator is always Idle
            _orchestrator.CurrentState.Should().Be(PipelineLifecycleState.Idle);
            _orchestrator.IsRecording.Should().BeFalse();
            _orchestrator.IsProcessing.Should().BeFalse();
        }
    }
}
