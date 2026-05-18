using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperVoice.Services
{
    /// <summary>
    /// Builds whisper-cli.exe arguments and manages the subprocess lifecycle.
    /// Extracted from MainWindow to honour SRP.
    /// All whisper.cpp C++ CLI syntax is centralised here.
    /// </summary>
    public class WhisperExecutionService
    {
        private readonly string _baseDir;
        private string WhisperExe => Path.Combine(_baseDir, "whisper-cli.exe");
        private string TempWavPath => Path.Combine(Path.GetTempPath(), "WhisperVoice_temp.wav");
        private string TempTxtPath => Path.Combine(Path.GetTempPath(), "WhisperVoice_temp.wav.txt");

        public WhisperExecutionService(string baseDir) => _baseDir = baseDir;

        // ── Public entry point ─────────────────────────────────────────────

        /// <summary>
        /// Runs whisper-cli.exe and returns the raw output text, or <c>null</c> on failure.
        /// All process output lines are forwarded through <paramref name="progress"/>.
        /// Writes stderr to <paramref name="logAction"/>.
        /// </summary>
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
            double noSpeechThreshold = 0.6)
        {
            if (File.Exists(TempTxtPath)) File.Delete(TempTxtPath);

            int threads = Math.Max(2, Environment.ProcessorCount - 1);

            var psi = new ProcessStartInfo
            {
                FileName = WhisperExe,
                WorkingDirectory = _baseDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // Modern .NET 8 structured argument collection isolation
            PopulateArgumentList(psi, modelPath, lang, isTranslate, techPrompt, threads,
                                 beamSize, bestOf, temperature, noSpeechThreshold);

            // Reconstruct full execution string representation strictly for laptop diagnostic logging
            var logBuilder = new StringBuilder("whisper-cli");
            foreach (var arg in psi.ArgumentList)
            {
                if (arg.Contains(" ") || arg.Contains("\""))
                    logBuilder.Append($" \"{arg.Replace("\"", "\\\"")}\"");
                else
                    logBuilder.Append($" {arg}");
            }
            logAction?.Invoke($"whisper-cli args: {logBuilder}");
            progress?.Report("🔍 Запуск Whisper...");

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var exitTcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                progress?.Report(e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                logAction?.Invoke($"[whisper stderr] {e.Data}");
                if (!e.Data.Contains('%') && !e.Data.Contains("whisper_"))
                    progress?.Report(e.Data);
            };

            process.Exited += (_, _) => exitTcs.TrySetResult(true);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cancelReg = token.Register(() =>
            {
                exitTcs.TrySetResult(false);
                KillProcessTree(process);
                logAction?.Invoke("Whisper process cancelled by user.");
            });

            try { await exitTcs.Task; }
            catch (OperationCanceledException) { return null; }

            token.ThrowIfCancellationRequested();

            int exitCode = process.ExitCode;
            if (exitCode != 0)
            {
                string err = exitCode switch
                {
                    unchecked((int)0xC0000135) => "Не найдена необходимая DLL (GGML/CUDA).",
                    unchecked((int)0xC0000005) => "Access violation / OOM — возможно VRAM переполнена.",
                    1 => "whisper-cli вернул код 1 — нехватка памяти или некорректный WAV.",
                    _ => $"whisper-cli завершился с кодом {exitCode}."
                };
                logAction?.Invoke($"whisper exit code {exitCode}: {err}");
                progress?.Report($"❌ Ошибка (код {exitCode})");
                throw new WhisperProcessException(exitCode, err);
            }

            if (!File.Exists(TempTxtPath))
            {
                logAction?.Invoke("Output file not found after successful exit.");
                return null;
            }

            return File.ReadAllText(TempTxtPath).Trim();
        }

        // ── Argument builder (whisper.cpp C++ CLI via ArgumentList) ────────
        private void PopulateArgumentList(
            ProcessStartInfo psi,
            string model, string lang, bool isTranslate, string prompt, int threads,
            int beamSize, int bestOf, double temperature, double noSpeechThreshold)
        {
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(model);

            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(TempWavPath);

            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add(lang);

            if (isTranslate) 
                psi.ArgumentList.Add("-tr");

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                psi.ArgumentList.Add("--prompt");
                psi.ArgumentList.Add(prompt);
            }

            psi.ArgumentList.Add("-otxt");
            psi.ArgumentList.Add("-nt");
            psi.ArgumentList.Add("-np");

            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(threads.ToString());

            psi.ArgumentList.Add("--beam-size");
            psi.ArgumentList.Add(beamSize.ToString());

            psi.ArgumentList.Add("--best-of");
            psi.ArgumentList.Add(bestOf.ToString());

            psi.ArgumentList.Add("--temperature");
            psi.ArgumentList.Add(temperature.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

            psi.ArgumentList.Add("--no-speech-thold");
            psi.ArgumentList.Add(noSpeechThreshold.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        }

        // ── Process utilities ──────────────────────────────────────────────
        private static void KillProcessTree(Process process)
        {
            try
            {
                var kill = new ProcessStartInfo(
                    "taskkill", $"/F /T /PID {process.Id}")
                { UseShellExecute = false, CreateNoWindow = true };
                Process.Start(kill)?.WaitForExit(3000);
            }
            catch
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    /// <summary>Thrown when whisper-cli.exe exits with a non-zero code.</summary>
    public class WhisperProcessException : Exception
    {
        public int ExitCode { get; }
        public WhisperProcessException(int exitCode, string message)
            : base(message) => ExitCode = exitCode;
    }
}