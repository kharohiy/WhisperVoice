// ============================================================
//  LoopbackSource.cs
//  WhisperVoice — WASAPI Loopback Capture
// ============================================================

using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperVoice.Services
{
    public sealed class LoopbackSource : IAudioSource
    {
        private static readonly DiagnosticLogger Log = DiagnosticLogger.Instance;
        private const string Comp = "LoopbackSource";

        private static readonly WaveFormat WhisperFormat = new WaveFormat(16_000, 16, 1);

        private Task? _workerTask;
        private CancellationTokenSource? _cts;

        private volatile bool _isRecording;
        private int _disposed;

        public bool IsRecording => _isRecording;

        public event Action<double>? PeakAvailable;
        public event Action? SilenceDetected;
        public event Action<Exception>? RecordingAborted;

        public double VadThreshold { get; set; } = 5.0;
        public TimeSpan VadSilenceTimeout { get; set; } = TimeSpan.FromSeconds(1.8);
        public TimeSpan VadGracePeriod { get; set; } = TimeSpan.FromSeconds(1.5);

        public bool StartRecording(string deviceId, string outputPath)
        {
            if (_isRecording) return false;

            Log.Info(Comp, $"StartRecording outputPath={outputPath}");

            try
            {
                _cts = new CancellationTokenSource();
                _isRecording = true;

                // CRITICAL ARCHITECTURE FIX: 
                // We move 100% of NAudio's lifecycle (Creation, Execution, Stopping, Disposal) 
                // onto a single background MTA thread. The WPF UI thread (STA) will NEVER 
                // touch the WasapiLoopbackCapture object, completely eliminating COM Deadlocks.
                _workerTask = Task.Run(() => AudioWorkerLoop(outputPath, _cts.Token));

                Log.Info(Comp, "Audio worker task spawned successfully.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(Comp, ex, "Failed to spawn audio worker task.");
                _isRecording = false;
                return false;
            }
        }

        public async Task StopRecordingAsync()
        {
            if (!_isRecording) return;

            Log.Info(Comp, "StopRecordingAsync: Signaling worker to stop...");
            _isRecording = false;
            _cts?.Cancel();

            if (_workerTask != null)
            {
                // We wait for the background thread to finish its cleanup.
                // If Windows Audio Engine hangs the background thread, we abandon it after 800ms
                // so the UI thread NEVER freezes.
                await Task.WhenAny(_workerTask, Task.Delay(800)).ConfigureAwait(false);
                _workerTask = null;
            }

            Log.Info(Comp, "StopRecordingAsync: UI thread unblocked and ready.");
        }

        public void StopRecording() => _ = StopRecordingAsync();

        /// <summary>
        /// This entire method runs on a background thread. It owns the WASAPI objects.
        /// </summary>
        private void AudioWorkerLoop(string outputPath, CancellationToken ct)
        {
            WasapiLoopbackCapture? capture = null;
            WaveFileWriter? writer = null;

            try
            {
                // 1. Initialize COM objects strictly on this background thread
                capture = new WasapiLoopbackCapture();
                var nativeFormat = capture.WaveFormat;

                var buffer = new BufferedWaveProvider(nativeFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(5),
                    DiscardOnBufferOverflow = true
                };

                // 2. Use WDL (Pure C#) instead of MediaFoundation to prevent ReadFromTransform deadlocks
                var wdlSample = new WdlResamplingSampleProvider(buffer.ToSampleProvider(), WhisperFormat.SampleRate).ToMono();
                var resampler = new SampleToWaveProvider16(wdlSample);

                writer = new WaveFileWriter(outputPath, WhisperFormat);

                // 3. VAD State
                var vadStopwatch = new System.Diagnostics.Stopwatch();
                long startTick = System.Diagnostics.Stopwatch.GetTimestamp();
                bool silenceFired = false;

                // 4. Data callback (Pushing data into the buffer)
                EventHandler<WaveInEventArgs> onDataAvailable = (s, e) =>
                {
                    if (e.BytesRecorded <= 0) return;

                    buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

                    double peak = CalculatePeak(e.Buffer, e.BytesRecorded, nativeFormat);
                    PeakAvailable?.Invoke(peak); // Fire-and-forget to UI

                    if (peak > VadThreshold)
                    {
                        vadStopwatch.Restart();
                        silenceFired = false;
                    }
                };

                capture.DataAvailable += onDataAvailable;
                capture.StartRecording();
                vadStopwatch.Start();

                Log.Info(Comp, "Background Worker: WASAPI Capture started.");

                int readBytes = 4096 * WhisperFormat.BlockAlign;
                byte[] readBuf = new byte[readBytes];

                // 5. Main Processing Loop
                while (!ct.IsCancellationRequested)
                {
                    // To prevent WDL resampler from spinning or blocking, we only read 
                    // if we have accumulated enough data in the buffer.
                    if (buffer.BufferedBytes >= nativeFormat.AverageBytesPerSecond / 10)
                    {
                        int read = resampler.Read(readBuf, 0, readBytes);
                        if (read > 0)
                        {
                            writer.Write(readBuf, 0, read);
                        }
                    }
                    else
                    {
                        // Sleep briefly to prevent 100% CPU core usage
                        Thread.Sleep(20);
                    }

                    // VAD processing
                    if (!silenceFired && _isRecording)
                    {
                        double secondsElapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - startTick) / (double)System.Diagnostics.Stopwatch.Frequency;
                        if (secondsElapsed >= VadGracePeriod.TotalSeconds && vadStopwatch.Elapsed >= VadSilenceTimeout)
                        {
                            silenceFired = true;
                            SilenceDetected?.Invoke();
                        }
                    }
                }

                // 6. Loop exited (Cancellation requested). Stop WASAPI gracefully.
                Log.Info(Comp, "Background Worker: Cancellation received. Stopping capture...");

                capture.DataAvailable -= onDataAvailable;
                capture.StopRecording(); // Safe: We are not on the UI thread!

                // Final flush of remaining buffer
                while (buffer.BufferedBytes > 0)
                {
                    int tail = resampler.Read(readBuf, 0, readBytes);
                    if (tail == 0) break;
                    writer.Write(readBuf, 0, tail);
                }

                Log.Info(Comp, "Background Worker: File closed and pipeline shut down cleanly.");
            }
            catch (Exception ex)
            {
                Log.Error(Comp, ex, "Background Worker: Unhandled exception.");
                RecordingAborted?.Invoke(ex);
            }
            finally
            {
                // 7. Guaranteed Disposal on the same thread
                try { writer?.Dispose(); } catch { }
                try { capture?.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _isRecording = false;
            _cts?.Cancel();
            _cts?.Dispose();
        }

        private static double CalculatePeak(byte[] buffer, int bytesRecorded, WaveFormat fmt)
        {
            float max = 0f;
            var span = buffer.AsSpan(0, bytesRecorded);

            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                var floats = MemoryMarshal.Cast<byte, float>(span);
                foreach (float s in floats)
                {
                    float abs = MathF.Abs(s);
                    if (abs > max) max = abs;
                }
            }
            else
            {
                var shorts = MemoryMarshal.Cast<byte, short>(span);
                foreach (short s in shorts)
                {
                    float abs = MathF.Abs(s / 32768f);
                    if (abs > max) max = abs;
                }
            }
            return Math.Min(100.0, Math.Sqrt(max) * 100.0);
        }
    }
}