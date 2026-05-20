using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperVoice.Services
{
    public class WhisperExecutionService : IWhisperExecutionService
    {
        public WhisperExecutionService()
        {
        }

        public void PopulateArgumentList(
            ProcessStartInfo psi,
            string modelPath,
            string audioPath,
            string lang,
            bool isTranslate,
            string techPrompt,
            int beamSize,
            int bestOf,
            double temperature,
            double noSpeechThreshold)
        {
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(modelPath);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(audioPath);
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add(lang);

            if (isTranslate)
            {
                psi.ArgumentList.Add("-tr");
            }

            if (!string.IsNullOrWhiteSpace(techPrompt))
            {
                psi.ArgumentList.Add("--prompt");
                psi.ArgumentList.Add(techPrompt);
            }

            // Always explicitly pass parameters from Settings, retaining user control
            psi.ArgumentList.Add("--beam-size");
            psi.ArgumentList.Add(beamSize.ToString());
            
            psi.ArgumentList.Add("--best-of");
            psi.ArgumentList.Add(bestOf.ToString());
            
            if (temperature > 0.0)
            {
                psi.ArgumentList.Add("--temperature");
                psi.ArgumentList.Add(temperature.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            }
            
            psi.ArgumentList.Add("-otxt");
        }

        public async Task<string?> RunAsync(
            string modelPath,
            string lang,
            bool isTranslate,
            string techPrompt,
            IProgress<string>? progress,
            Action<string>? logAction,
            CancellationToken token,
            int beamSize = 5,
            int bestOf = 5,
            double temperature = 0.0,
            double noSpeechThreshold = 0.6,
            Action<bool>? vulkanStatusCallback = null)
        {
            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "whisper-cli.exe");
            if (!File.Exists(exePath))
            {
                logAction?.Invoke($"Error: whisper-cli.exe missing at {exePath}");
                progress?.Report("Error: whisper-cli.exe missing!");
                return null;
            }

            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                logAction?.Invoke($"Error: Model file is invalid or missing at {modelPath}");
                return null;
            }

            string tempWav = Path.Combine(Path.GetTempPath(), "WhisperVoice_temp.wav");
            if (!File.Exists(tempWav))
            {
                logAction?.Invoke("Error: Temp WAV file missing before execution.");
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetTempPath()
            };

            PopulateArgumentList(psi, modelPath, tempWav, lang, isTranslate, techPrompt, beamSize, bestOf, temperature, noSpeechThreshold);

            var logBuilder = new StringBuilder("whisper-cli");
            bool maskNext = false;
            foreach (var arg in psi.ArgumentList)
            {
                if (maskNext)
                {
                    logBuilder.Append(" [REDACTED_PROMPT]");
                    maskNext = false;
                    continue;
                }
                if (arg == "--prompt")
                {
                    logBuilder.Append(" --prompt");
                    maskNext = true;
                    continue;
                }
                if (arg.Contains(" ") || arg.Contains("\""))
                    logBuilder.Append($" \"{arg.Replace("\"", "\\\"")}\"");
                else
                    logBuilder.Append($" {arg}");
            }
            logAction?.Invoke($"whisper-cli args: {logBuilder}");
            progress?.Report("рџ”Ќ Р—Р°РїСѓСЃРє Whisper...");

            bool localVulkanFound = false;
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var exitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                progress?.Report(e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                bool isSystemInfo = e.Data.Contains("ggml") || e.Data.Contains("vulkan") || e.Data.Contains("Vulkan") || e.Data.Contains("whisper_") || e.Data.Contains("init") || e.Data.Contains("error") || e.Data.Contains("failed") || e.Data.Contains("system") || (e.Data.Contains("main:") && !e.Data.Contains("-->"));
                
                if (e.Data.Contains("ggml_vulkan") || e.Data.Contains("vulkan") || e.Data.Contains("Vulkan"))
                {
                    localVulkanFound = true;
                }

                if (isSystemInfo) logAction?.Invoke($"[whisper stderr] {e.Data}");
                if (!e.Data.Contains('%') && !e.Data.Contains("whisper_"))
                    progress?.Report(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var registration = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
                exitTcs.TrySetCanceled();
            });

            process.Exited += (_, _) => exitTcs.TrySetResult(true);

            try { await exitTcs.Task; }
            catch (OperationCanceledException) { return null; }

            vulkanStatusCallback?.Invoke(localVulkanFound);
            token.ThrowIfCancellationRequested();

            int exitCode = process.ExitCode;
            logAction?.Invoke($"whisper-cli exited with code {exitCode}");

            string? foundPath = null;
            string[] possiblePaths = {
                tempWav + ".txt",
                Path.ChangeExtension(tempWav, ".txt"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WhisperVoice_temp.wav.txt"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WhisperVoice_temp.txt")
            };

            foreach (var p in possiblePaths) {
                if (File.Exists(p)) {
                    foundPath = p;
                    break;
                }
            }

            if (foundPath == null)
            {
                logAction?.Invoke("Error: Transcription output file missing. Checked multiple path variants.");
                return string.Empty;
            }

            string result = await File.ReadAllTextAsync(foundPath, Encoding.UTF8);
            try { File.Delete(foundPath); } catch { }
            
            return result;
        }
    }
}