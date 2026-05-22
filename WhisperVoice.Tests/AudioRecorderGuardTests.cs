using System;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using Xunit;

namespace WhisperVoice.Tests
{
    /// <summary>
    /// Tests for AudioRecorder guard logic.
    /// These tests do NOT require real audio hardware — they test the guards
    /// that prevent double-start and double-dispose bugs.
    /// </summary>
    public class AudioRecorderGuardTests
    {
        // ── Issue 1: StartRecording without IsRecording check ────────────────
        // If StartRecording is called twice, the second call overwrites
        // _capture without Disposing the previous one → memory leak + crash.

        [Fact]
        public void StartRecording_WhenAlreadyRecording_ReturnsFalse()
        {
            // Arrange
            var recorder = new AudioRecorder();
            // Simulate that recording is already in progress (without real hardware)
            // The test verifies the guard condition behavior via public IsRecording
            // First ensure IsRecording == false by default
            Assert.False(recorder.IsRecording);

            // We cannot call the real StartRecording without hardware,
            // so we test via ForceRecordingState (new internal method)
            recorder.ForceRecordingState(true);
            Assert.True(recorder.IsRecording);

            // Act — attempt to start recording while already recording
            bool result = recorder.StartRecording("fake-device-id", "fake-path.wav");

            // Assert — must return false, do not throw exception
            Assert.False(result);
        }

        [Fact]
        public void StopRecording_WhenNotStarted_DoesNotThrow()
        {
            // Arrange
            var recorder = new AudioRecorder();
            Assert.False(recorder.IsRecording);

            // Act & Assert — calling stop without start must not throw
            var ex = Record.Exception(() => recorder.StopRecording());
            Assert.Null(ex);
        }

        [Fact]
        public async Task StopRecordingAsync_WhenNotStarted_CompletesImmediately()
        {
            // Arrange
            var recorder = new AudioRecorder();

            // Act
            var task = recorder.StopRecordingAsync();

            // Assert — the task must complete immediately (returns Task.CompletedTask)
            await Task.WhenAny(task, Task.Delay(500));
            Assert.True(task.IsCompleted, "StopRecordingAsync should complete immediately when not recording");
        }

        // ── Issue 2: Double Dispose ───────────────────────────────────────
        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            // Arrange
            var recorder = new AudioRecorder();

            // Act — double Dispose must not throw ObjectDisposedException
            var ex = Record.Exception(() =>
            {
                recorder.Dispose();
                recorder.Dispose();
            });

            // Assert
            Assert.Null(ex);
        }

        [Fact]
        public void Dispose_ThenStopRecording_DoesNotThrow()
        {
            // Arrange
            var recorder = new AudioRecorder();
            recorder.Dispose();

            // Act — calling methods after Dispose must not throw
            var ex = Record.Exception(() => recorder.StopRecording());
            Assert.Null(ex);
        }

        // ── CalculatePeak: now public static, testing edge cases
        [Fact]
        public void CalculatePeak_EmptyBuffer_ReturnsZero()
        {
            var format = new WaveFormat(16000, 16, 1);
            double peak = AudioRecorder.CalculatePeak(new byte[0], 0, format);
            Assert.Equal(0.0, peak);
        }

        [Fact]
        public void CalculatePeak_SilentBuffer_ReturnsNearZero()
        {
            var format = new WaveFormat(16000, 16, 1);
            // 100 bytes of zeros = silence
            var buffer = new byte[100];
            double peak = AudioRecorder.CalculatePeak(buffer, buffer.Length, format);
            Assert.True(peak < 1.0, $"Silent buffer should return near-zero peak, got {peak}");
        }

        [Fact]
        public void CalculatePeak_MaxAmplitudeBuffer_Returns100()
        {
            var format = new WaveFormat(16000, 16, 1);
            // Fill buffer with maximum Int16 values
            var buffer = new byte[200];
            for (int i = 0; i < buffer.Length; i += 4)
            {
                // Alternate +32767 and -32768 for max AC
                BitConverter.GetBytes((short)32767).CopyTo(buffer, i);
                if (i + 2 < buffer.Length)
                    BitConverter.GetBytes((short)-32768).CopyTo(buffer, i + 2);
            }
            double peak = AudioRecorder.CalculatePeak(buffer, buffer.Length, format);
            Assert.True(peak > 50.0, $"Max amplitude buffer should return high peak, got {peak}");
        }
    }
}
