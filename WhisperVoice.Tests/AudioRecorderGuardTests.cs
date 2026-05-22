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
        // ── Проблема 1: StartRecording без проверки IsRecording ────────────────
        // Если вызвать StartRecording дважды — второй вызов перезаписывает
        // _capture без Dispose предыдущего → утечка + crash.

        [Fact]
        public void StartRecording_WhenAlreadyRecording_ReturnsFalse()
        {
            // Arrange
            var recorder = new AudioRecorder();
            // Симулируем что запись уже идёт (без реального устройства)
            // Тест проверяет поведение guard-условия через публичный IsRecording
            // Сначала убеждаемся что IsRecording == false по умолчанию
            Assert.False(recorder.IsRecording);

            // Мы не можем вызвать настоящий StartRecording без железа,
            // поэтому тестируем через ForceRecordingState (новый internal метод)
            recorder.ForceRecordingState(true);
            Assert.True(recorder.IsRecording);

            // Act — попытка запустить запись пока уже идёт
            bool result = recorder.StartRecording("fake-device-id", "fake-path.wav");

            // Assert — должен вернуть false, не бросить исключение
            Assert.False(result);
        }

        [Fact]
        public void StopRecording_WhenNotStarted_DoesNotThrow()
        {
            // Arrange
            var recorder = new AudioRecorder();
            Assert.False(recorder.IsRecording);

            // Act & Assert — вызов stop без start не должен бросать
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

            // Assert — задача должна завершиться мгновенно (уже возвращает Task.CompletedTask)
            await Task.WhenAny(task, Task.Delay(500));
            Assert.True(task.IsCompleted, "StopRecordingAsync should complete immediately when not recording");
        }

        // ── Проблема 2: двойной Dispose ───────────────────────────────────────
        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            // Arrange
            var recorder = new AudioRecorder();

            // Act — двойной Dispose не должен бросать ObjectDisposedException
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

            // Act — вызов методов после Dispose не должен бросать
            var ex = Record.Exception(() => recorder.StopRecording());
            Assert.Null(ex);
        }

        // ── CalculatePeak: уже публичный статический, тестируем граничные случаи
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
            // 100 байт нулей = тишина
            var buffer = new byte[100];
            double peak = AudioRecorder.CalculatePeak(buffer, buffer.Length, format);
            Assert.True(peak < 1.0, $"Silent buffer should return near-zero peak, got {peak}");
        }

        [Fact]
        public void CalculatePeak_MaxAmplitudeBuffer_Returns100()
        {
            var format = new WaveFormat(16000, 16, 1);
            // Заполняем буфер максимальными значениями Int16
            var buffer = new byte[200];
            for (int i = 0; i < buffer.Length; i += 4)
            {
                // Чередуем +32767 и -32768 для максимального AC
                BitConverter.GetBytes((short)32767).CopyTo(buffer, i);
                if (i + 2 < buffer.Length)
                    BitConverter.GetBytes((short)-32768).CopyTo(buffer, i + 2);
            }
            double peak = AudioRecorder.CalculatePeak(buffer, buffer.Length, format);
            Assert.True(peak > 50.0, $"Max amplitude buffer should return high peak, got {peak}");
        }
    }
}
