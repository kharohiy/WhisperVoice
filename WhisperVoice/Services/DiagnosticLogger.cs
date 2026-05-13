// ============================================================
//  DiagnosticLogger.cs
//  WhisperVoice – production diagnostic logger
//
//  Design:
//    • ConcurrentQueue<string> as the hot-path write buffer.
//      WASAPI DataAvailable (~25×/sec) enqueues with zero
//      contention. A single background Task drains to disk.
//    • SemaphoreSlim(0,1) wakes the drainer only when work
//      exists, so idle CPU cost is ~zero.
//    • File opened once with FileShare.Read so Notepad can
//      tail the log live while the app is running.
//    • Primary path:  AppDomain.CurrentDomain.BaseDirectory
//      Fallback path: Environment.SpecialFolder.Desktop
//      (handles UAC-protected Program Files installs)
// ============================================================

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperVoice
{
    public sealed class DiagnosticLogger : IDisposable
    {
        // ── Singleton ────────────────────────────────────────────────────────
        private static readonly Lazy<DiagnosticLogger> _instance =
            new(() => new DiagnosticLogger(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static DiagnosticLogger Instance => _instance.Value;

        // ── State ────────────────────────────────────────────────────────────
        private readonly ConcurrentQueue<string> _queue = new();
        private readonly SemaphoreSlim           _signal = new(0, 1);
        private readonly CancellationTokenSource _cts    = new();
        private readonly string                  _logPath;
        private          StreamWriter?           _writer;
        private          bool                    _disposed;

        // ── Log Levels ───────────────────────────────────────────────────────
        public enum Level { TRACE, INFO, WARN, ERROR }

        // ── Constructor (private – use Instance) ─────────────────────────────
        private DiagnosticLogger()
        {
            _logPath = ResolveLogPath();
            OpenWriter();
            Task.Factory.StartNew(DrainLoop, _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);

            // First entry: session header
            Info("DiagnosticLogger", $"═══ Session started ═══  Path={_logPath}");
            Info("DiagnosticLogger", $"OS={Environment.OSVersion}  CPU={Environment.ProcessorCount}  " +
                                     $"CLR={Environment.Version}  PID={Environment.ProcessId}");
        }

        // ── Public API ───────────────────────────────────────────────────────
        public void Trace(string component, string message) => Enqueue(Level.TRACE, component, message);
        public void Info (string component, string message) => Enqueue(Level.INFO,  component, message);
        public void Warn (string component, string message) => Enqueue(Level.WARN,  component, message);
        public void Error(string component, string message) => Enqueue(Level.ERROR, component, message);

        public void Error(string component, Exception ex, string context = "")
        {
            string prefix = string.IsNullOrEmpty(context) ? "" : $"{context} → ";
            Enqueue(Level.ERROR, component,
                $"{prefix}{ex.GetType().Name}: {ex.Message}  " +
                $"HRESULT=0x{ex.HResult:X8}  Stack={ex.StackTrace?.Split('\n')[0]?.Trim()}");
        }

        /// <summary>Returns the full path of the active log file.</summary>
        public string LogPath => _logPath;

        // ── Internal helpers ─────────────────────────────────────────────────
        private void Enqueue(Level level, string component, string message)
        {
            if (_disposed) return;

            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] " +
                          $"[T:{Thread.CurrentThread.ManagedThreadId,3}] " +
                          $"[{level,-5}] " +
                          $"[{component}] {message}";

            _queue.Enqueue(line);

            // Signal the drainer (non-blocking; cap at 1 so we don't
            // overflow the semaphore when many threads enqueue at once)
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }

        private async Task DrainLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                FlushQueue();
            }

            // Final drain on shutdown
            FlushQueue();
            _writer?.Flush();
            _writer?.Dispose();
        }

        private void FlushQueue()
        {
            if (_writer == null) return;
            while (_queue.TryDequeue(out string? line))
            {
                try   { _writer.WriteLine(line); }
                catch { /* swallow — never throw from logger */ }
            }
            try { _writer.Flush(); } catch { }
        }

        private void OpenWriter()
        {
            try
            {
                _writer = new StreamWriter(
                    new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read),
                    System.Text.Encoding.UTF8, bufferSize: 4096, leaveOpen: false);
            }
            catch (Exception ex)
            {
                // Last-resort: stderr so the developer sees it somewhere
                Console.Error.WriteLine($"[DiagnosticLogger] Cannot open log file: {ex.Message}");
            }
        }

        private static string ResolveLogPath()
        {
            const string fileName = "whisper_diagnostic.log";

            // Primary: installation directory
            try
            {
                string basePath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, fileName);

                // Probe write access before committing
                using var probe = new FileStream(basePath, FileMode.Append,
                    FileAccess.Write, FileShare.Read);
                return basePath;
            }
            catch (UnauthorizedAccessException) { /* fall through */ }
            catch (IOException)                 { /* fall through */ }

            // Fallback: Desktop (always writable)
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Info("DiagnosticLogger", "═══ Session ended ═══");
            _cts.Cancel();
        }
    }
}
