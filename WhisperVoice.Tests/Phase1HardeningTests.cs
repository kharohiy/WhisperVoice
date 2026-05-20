using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using WhisperVoice.Services;

namespace WhisperVoice.Tests
{
    public class Phase1HardeningTests
    {
        [Fact]
        public void TransientDataCleaner_ShouldPurgeAllTemporaryFilesAndOrphanedModels()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            
            var modelsDir = Path.Combine(tempDir, "models");
            Directory.CreateDirectory(modelsDir);
            
            var tempWav = Path.Combine(tempDir, "temp.wav");
            var tempTxt = Path.Combine(tempDir, "temp.wav.txt");
            var orphanedModel = Path.Combine(modelsDir, "model.bin.part");
            var validModel = Path.Combine(modelsDir, "model.bin");
            
            File.WriteAllText(tempWav, "dummy wav data");
            File.WriteAllText(tempTxt, "dummy txt data");
            File.WriteAllText(orphanedModel, "partial data");
            File.WriteAllText(validModel, "valid model data");
            
            // Act
            TransientDataCleaner.Cleanup(tempWav, tempTxt, modelsDir);
            
            // Assert
            File.Exists(tempWav).Should().BeFalse("temp.wav must be destroyed");
            File.Exists(tempTxt).Should().BeFalse("temp.txt must be destroyed");
            File.Exists(orphanedModel).Should().BeFalse(".part downloads must be purged to avoid corruption");
            File.Exists(validModel).Should().BeTrue("valid .bin models should not be touched");
            
            // Cleanup test
            Directory.Delete(tempDir, true);
        }

        [Fact]
        public async Task WhisperExecutionService_RunAsync_WithInvalidModel_ShouldReturnNullAndNotCrash()
        {
            // Arrange
            var service = new WhisperExecutionService();
            var invalidModelPath = Path.Combine(Path.GetTempPath(), "does_not_exist.bin");
            bool errorLogged = false;
            
            Action<string> logger = msg => 
            {
                if (msg.Contains("invalid or missing")) errorLogged = true;
            };

            // Act
            var result = await service.RunAsync(
                modelPath: invalidModelPath,
                lang: "en",
                isTranslate: false,
                techPrompt: "",
                progress: null,
                logAction: logger,
                token: CancellationToken.None);
            
            // Assert
            result.Should().BeNull("execution should fail fast and return null when model is missing");
            errorLogged.Should().BeTrue("error should be explicitly logged");
        }
    }
}
