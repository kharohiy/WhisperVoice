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
        private string TempWavPath => Path.Combine(_baseDir, "temp.wav");
        private string TempTxtPath => Path.Combine(_baseDir, "temp.wav.txt");

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
            bool   isTranslate,
            string techPrompt,
            IProgress<string> progress,
            Action<string>    logAction,
            CancellationToken token)
        {
            if (File.Exists(TempTxtPath)) File.Delete(TempTxtPath);

            int threads = Math.Max(2, Environment.ProcessorCount - 1);
            string args = BuildArgs(modelPath, lang, isTranslate, techPrompt, threads);

            logAction($"whisper-cli args: {args}");
            progress.Report("🔍 Запуск Whisper...");

            var psi = new ProcessStartInfo
            {
                FileName               = WhisperExe,
                Arguments              = args,
                WorkingDirectory       = _baseDir,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var exitTcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                progress.Report(e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                logAction($"[whisper stderr] {e.Data}");
                // Surface meaningful stderr lines to the status label
                if (!e.Data.Contains('%') && !e.Data.Contains("whisper_"))
                    progress.Report(e.Data);
            };

            process.Exited += (_, _) => exitTcs.TrySetResult(true);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cancelReg = token.Register(() =>
            {
                exitTcs.TrySetCanceled();
                KillProcessTree(process);
                logAction("Whisper process cancelled by user.");
            });

            try   { await exitTcs.Task; }
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
                logAction($"whisper exit code {exitCode}: {err}");
                progress.Report($"❌ Ошибка (код {exitCode})");
                throw new WhisperProcessException(exitCode, err);
            }

            if (!File.Exists(TempTxtPath))
            {
                logAction("Output file not found after successful exit.");
                return null;
            }

            return File.ReadAllText(TempTxtPath).Trim();
        }

        // ── Argument builder (whisper.cpp C++ CLI) ─────────────────────────
        /// <summary>
        /// Builds the argument string for whisper-cli.exe.
        /// RULE: always use short flags (-m, -f, -l, -tr, -otxt, -nt, -np, -t).
        /// Never use --model / --language — those are Python whisper flags.
        /// </summary>
        private string BuildArgs(
            string model, string lang, bool isTranslate,
            string prompt,  int threads)
        {
            var sb = new StringBuilder();
            sb.Append($"-m \"{model}\"");
            sb.Append($" -f \"{TempWavPath}\"");
            sb.Append($" -l {lang}");
            if (isTranslate) sb.Append(" -tr");
            if (!string.IsNullOrWhiteSpace(prompt))
                sb.Append($" --prompt \"{prompt}\"");
            sb.Append(" -otxt -nt -np");
            sb.Append($" -t {threads}");
            return sb.ToString();
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
