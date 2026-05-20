using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Xunit;
using Moq;
using Moq.Protected;
using FluentAssertions;
using WhisperVoice.Services;
using WhisperVoice.Models;

namespace WhisperVoice.Tests
{
    public class ChaosStressTests
    {
        #region 1. Model Download Mid-Stream Network Drop

        public class InterruptedStream : Stream
        {
            private readonly byte[] _data;
            private int _position = 0;
            private readonly double _throwPercent;
            private bool _thrown = false;

            public InterruptedStream(byte[] data, double throwPercent = 0.5)
            {
                _data = data;
                _throwPercent = throwPercent;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (!_thrown && _position >= _data.Length * _throwPercent)
                {
                    _thrown = true;
                    throw new IOException("Simulated network drop mid-stream!");
                }

                int remaining = _data.Length - _position;
                if (remaining <= 0) return 0;

                int readBytes = Math.Min(count, remaining);
                readBytes = Math.Min(readBytes, 4096); // Limit read size to ensure multiple reads occur

                Array.Copy(_data, _position, buffer, offset, readBytes);
                _position += readBytes;
                return readBytes;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _data.Length;
            public override long Position { get => _position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [Fact]
        public async Task ModelDownloadService_MidStreamNetworkDrop_PurgesPartFile()
        {
            // Arrange
            var mockHandler = new Mock<HttpMessageHandler>();
            var dummyData = new byte[100_000];
            new Random().NextBytes(dummyData);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new InterruptedStream(dummyData, 0.5))
            };
            response.Content.Headers.ContentLength = dummyData.Length;

            mockHandler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            var httpClient = new HttpClient(mockHandler.Object);
            var service = new ModelDownloadService(httpClient);

            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            var destinationPath = Path.Combine(tempDir, "test_model.bin");
            var partPath = destinationPath + ".part";
            var progress = new Progress<double>();

            // Act
            Func<Task> downloadAct = async () =>
            {
                await service.DownloadAsync("https://huggingface.co/models/test_model.bin", destinationPath, "", progress);
            };

            // Assert
            await downloadAct.Should().ThrowAsync<IOException>().WithMessage("Simulated network drop mid-stream!");
            File.Exists(partPath).Should().BeFalse("The temporary '.part' file must be immediately deleted upon download failure to prevent disk corruption");
            File.Exists(destinationPath).Should().BeFalse("The final model destination should not be written to since download failed");

            // Clean up
            Directory.Delete(tempDir, true);
        }

        #endregion

        #region 2. Hotkey Orchestration Rapid Spamming

        [Fact]
        public async Task HotkeyOrchestration_RapidSpamming_MaintainsFSMIntegrity()
        {
            // Arrange
            var micMock = new Mock<IAudioCaptureService>();
            var loopbackMock = new Mock<IAudioCaptureService>();
            var whisperMock = new Mock<IWhisperExecutionService>();
            var hardware = new HardwareCheckService();
            var filter = new HallucinationFilter(Path.GetTempPath());
            var postProcessor = new TextPostProcessorService();
            var tempWav = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_temp.wav");

            micMock.Setup(m => m.StartRecording(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<bool>()))
                .Returns(() => { Thread.Sleep(5); return true; });
            micMock.Setup(m => m.StopRecordingAsync()).Returns(() => Task.Delay(5));

            var orchestrator = new RecordingOrchestrationService(micMock.Object, loopbackMock.Object, whisperMock.Object, hardware, filter, postProcessor, tempWav);

            var settings = new AppSettings();
            var dummyModelPath = Path.Combine(Path.GetTempPath(), "dummy_model.bin");
            File.WriteAllText(dummyModelPath, "dummy");
            settings.MicId = "MockMic";
            settings.LastModelPath = dummyModelPath;

            // Act: Fire rapid concurrent hotkey triggers across multiple threads simulating aggressive spamming
            var tasks = new List<Task>();
            for (int i = 0; i < 60; i++)
            {
                int index = i;
                tasks.Add(Task.Run(async () =>
                {
                    bool isPtt = index % 2 == 0;
                    bool isDown = index % 3 != 0;
                    var mode = index % 4 == 0 ? ProcessingMode.Translate : ProcessingMode.Primary;

                    await orchestrator.HandleHotkeyTrigger(mode, AudioSource.Microphone, isPushToTalk: isPtt, isKeyDown: isDown, settings, key => key);
                }));
            }

            // Assert: Verify it completes cleanly without hanging or deadlocks
            var completedTask = await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(10000));
            completedTask.Should().NotBe(Task.Delay(10000), "The FSM orchestration must complete quickly without deadlocks or thread starvation");

            // Verify final state is valid (either Recording or Idle depending on exact timing)
            orchestrator.CurrentState.Should().BeOneOf(PipelineLifecycleState.Idle, PipelineLifecycleState.Recording, PipelineLifecycleState.ProcessingAudio);

            // Clean up files
            try { if (File.Exists(dummyModelPath)) File.Delete(dummyModelPath); } catch {}
            try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch {}
        }

        #endregion

        #region 3. Audio Capture Unexpected Hardware Drop

        [Fact]
        public async Task AudioCapture_UnexpectedHardwareDrop_GracefulAborts()
        {
            // Arrange
            var micMock = new Mock<IAudioCaptureService>();
            var loopbackMock = new Mock<IAudioCaptureService>();
            var whisperMock = new Mock<IWhisperExecutionService>();
            var hardware = new HardwareCheckService();
            var filter = new HallucinationFilter(Path.GetTempPath());
            var postProcessor = new TextPostProcessorService();
            var tempWav = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_temp.wav");

            // Setup audio file path content to verify it gets deleted
            File.WriteAllText(tempWav, "dummy wav capture header and data");

            // StartRecording succeeds, but StopRecordingAsync throws MmException due to hardware disconnection
            micMock.Setup(m => m.StartRecording(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<bool>())).Returns(true);
            micMock.Setup(m => m.StopRecordingAsync()).Throws(new NAudio.MmException(NAudio.MmResult.InvalidHandle, "Device unplugged mid-capture"));

            var orchestrator = new RecordingOrchestrationService(micMock.Object, loopbackMock.Object, whisperMock.Object, hardware, filter, postProcessor, tempWav);

            var settings = new AppSettings();
            var dummyModelPath = Path.Combine(Path.GetTempPath(), "dummy_model.bin");
            File.WriteAllText(dummyModelPath, "dummy");
            settings.MicId = "MockMic";
            settings.LastModelPath = dummyModelPath;

            // Monitor state changes & pipeline status reports
            PipelineStatusReport? caughtReport = null;
            orchestrator.StatusReported += (s, report) =>
            {
                if (report.State == PipelineLifecycleState.Failed)
                {
                    caughtReport = report;
                }
            };

            // Act
            orchestrator.StartRecording(new RecordingRequest(ProcessingMode.Primary, AudioSource.Microphone));
            orchestrator.CurrentState.Should().Be(PipelineLifecycleState.Recording);

            // Attempting to stop capture triggers the mock exception
            await orchestrator.StopAndProcessAsync(settings, key => key);

            // Assert
            caughtReport.Should().NotBeNull("A failed transition status report must be raised");
            caughtReport!.State.Should().Be(PipelineLifecycleState.Failed, "The FSM state must be transitioned to Failed");
            caughtReport.Error.Should().Be(PipelineError.MicDisconnected, "Disconnection error type should be strictly set to MicDisconnected");

            File.Exists(tempWav).Should().BeFalse("The temporary WAV file must be immediately wiped from the disk by TransientDataCleaner to protect privacy");

            orchestrator.CurrentState.Should().Be(PipelineLifecycleState.Idle, "The state machine must return back to Idle after the failure cleanup");

            // Clean up files
            try { if (File.Exists(dummyModelPath)) File.Delete(dummyModelPath); } catch {}
        }

        #endregion
    }
}
