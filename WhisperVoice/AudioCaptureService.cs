using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WhisperVoice.Services
{
    public class AudioCaptureService : IAudioCaptureService, IDisposable
    {
        private static readonly DiagnosticLogger Log = DiagnosticLogger.Instance;
        private const string Comp = "AudioCaptureService";

        // Работаем через интерфейс вместо конкретного AudioRecorder
        private readonly IAudioSource _source;
        private WasapiCapture? _silentCapture;
        private MMDevice? _device;
        private DateTime _lastSilentPeak = DateTime.MinValue;
        private readonly bool _loopbackMode;

        public bool IsRecording => _source.IsRecording;

        public bool IsDeviceAttached
        {
            get
            {
                if (_device == null) return false;

                try
                {
                    var state = _device.State;
                    if (state != DeviceState.Active)
                    {
                        Log.Warn(Comp, $"IsDeviceAttached: device state={state} — detaching");
                        DetachDevice();
                        return false;
                    }
                    return true;
                }
                catch (COMException ex)
                {
                    Log.Error(Comp, ex, "IsDeviceAttached COMException — detaching");
                    DetachDevice();
                    return false;
                }
                catch (Exception ex)
                {
                    Log.Error(Comp, ex, "IsDeviceAttached unexpected exception");
                    return false;
                }
            }
        }

        public Action<Exception>? RecordingAborted { get; internal set; }

        public event Action<double>? PeakAvailable;
        public event Action? SilenceDetected;
        public event Action<float>? VolumeChanged;
        public event Action? DeviceDisconnected;

        /// <summary>
        /// Конструктор теперь принимает флаг режима.
        /// </summary>
        /// <param name="loopbackMode">true для захвата системного звука, false для микрофона.</param>
        public AudioCaptureService(bool loopbackMode = false)
        {
            _loopbackMode = loopbackMode;

            // Выбор стратегии захвата
            if (_loopbackMode)
            {
                _source = new LoopbackSource();
            }
            else
            {
                // AudioRecorder должен реализовывать IAudioSource
                _source = new AudioRecorder();
            }

            // Переподписываем события от источника на сервис
            _source.PeakAvailable += val => PeakAvailable?.Invoke(val);
            _source.SilenceDetected += () => SilenceDetected?.Invoke();
            _source.RecordingAborted += OnRecordingAborted;

            Log.Info(Comp, $"AudioCaptureService constructed — Mode: {(_loopbackMode ? "LOOPBACK" : "MICROPHONE")}");
        }

        public bool AttachDevice(string micId)
        {
            // Для режима Loopback выбор устройства микрофона не нужен для записи, 
            // но мы оставляем логику для работы индикатора громкости (SilentCapture)
            Log.Info(Comp, $"AttachDevice called  micId={micId}");

            try
            {
                DetachDevice();

                using var enumerator = new MMDeviceEnumerator();
                _device = enumerator.GetDevice(micId);

                Log.Info(Comp, $"Device resolved: FriendlyName=\"{_device.FriendlyName}\"  " +
                               $"State={_device.State}  ID={_device.ID}");

                if (_device.State != DeviceState.Active)
                {
                    Log.Warn(Comp, $"AttachDevice: device not Active (State={_device.State}) — aborting");
                    _device = null;
                    return false;
                }

                _silentCapture = new WasapiCapture(_device, true, 50);
                _silentCapture.DataAvailable += SilentCapture_DataAvailable;
                _silentCapture.StartRecording();

                _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
                float vol = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
                VolumeChanged?.Invoke(vol);

                Log.Info(Comp, $"AttachDevice SUCCESS  " +
                               $"SilentCapture={_silentCapture.WaveFormat.SampleRate}Hz  " +
                               $"MasterVolume={vol:P0}");
                return true;
            }
            catch (COMException ex)
            {
                Log.Error(Comp, ex, "AttachDevice COMException");
                HandleDeviceFailure();
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(Comp, ex, "AttachDevice unexpected exception");
                return false;
            }
        }

        private void DetachDevice()
        {
            Log.Trace(Comp, $"DetachDevice called  _device={(_device != null ? "EXISTS" : "NULL")}");

            if (_device != null)
            {
                try 
                { 
                    _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; 
                } 
                catch (Exception ex) 
                { 
                    Log.Error(Comp, ex, "Exception unsubscribing from OnVolumeNotification"); 
                }

                try
                {
                    _device.Dispose();
                    Log.Trace(Comp, "DetachDevice: _device disposed successfully");
                }
                catch (COMException ex)
                {
                    Log.Warn(Comp, $"DetachDevice: COMException disposing _device: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Log.Warn(Comp, $"DetachDevice: unexpected exception disposing _device: {ex.Message}");
                }
                finally
                {
                    _device = null;
                }
            }

            try
            {
                if (_silentCapture != null)
                {
                    _silentCapture.StopRecording();
                    _silentCapture.Dispose();
                    _silentCapture = null;
                    Log.Trace(Comp, "DetachDevice: silent capture stopped and disposed");
                }
            }
            catch (Exception ex)
            {
                Log.Warn(Comp, $"DetachDevice: exception disposing silent capture: {ex.Message}");
            }
        }

        private void OnVolumeNotification(AudioVolumeNotificationData data)
            => VolumeChanged?.Invoke(data.MasterVolume);

        public float GetVolume()
        {
            try { return _device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0f; }
            catch (COMException ex)
            {
                Log.Error(Comp, ex, "GetVolume COMException");
                HandleDeviceFailure();
                return 0f;
            }
            catch (Exception ex) { Log.Error(Comp, ex, "GetVolume general exception"); return 0f; }
        }

        public void SetVolume(float scalar)
        {
            try { if (_device != null) _device.AudioEndpointVolume.MasterVolumeLevelScalar = scalar; }
            catch (COMException ex)
            {
                Log.Error(Comp, ex, "SetVolume COMException");
                HandleDeviceFailure();
            }
            catch (Exception ex) { Log.Error(Comp, ex, "General exception in AudioCaptureService"); }
        }

        private void HandleDeviceFailure()
        {
            Log.Error(Comp, "HandleDeviceFailure called — raising DeviceDisconnected");
            DetachDevice();
            DeviceDisconnected?.Invoke();
        }

        private void SilentCapture_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_source.IsRecording) return;

            var now = DateTime.UtcNow;
            if ((now - _lastSilentPeak).TotalMilliseconds < 40) return;
            _lastSilentPeak = now;

            if (_silentCapture != null && _device != null)
            {
                try
                {
                    // Используем статический метод из AudioRecorder для расчета пика
                    double peak = AudioRecorder.CalculatePeak(e.Buffer, e.BytesRecorded, _silentCapture.WaveFormat);
                    PeakAvailable?.Invoke(peak);
                }
                catch (Exception ex)
                {
                    Log.Warn(Comp, $"SilentCapture peak calc failed: {ex.Message}");
                }
            }
        }

        public bool StartRecording(string micId, string outputPath, double vadThreshold, double vadSilenceSeconds, bool vadEnabled = true)
        {
            Log.Info(Comp, $"StartRecording (mode={(_loopbackMode ? "LOOPBACK" : "MICROPHONE")}) out={outputPath}");

            try { _silentCapture?.StopRecording(); } catch (Exception ex) { Log.Error(Comp, ex, "General exception in AudioCaptureService"); }

            // Configure VAD parameters for the current source
            _source.VadThreshold = vadThreshold;
            _source.VadSilenceTimeout = TimeSpan.FromSeconds(vadSilenceSeconds);
            _source.VadEnabled = vadEnabled;

            // Запуск записи. В режиме Loopback micId будет проигнорирован внутри.
            bool result = _source.StartRecording(micId, outputPath);

            Log.Info(Comp, $"StartRecording result={result}");
            return result;
        }

        public async Task StopRecordingAsync()
        {
            Log.Info(Comp, "StopRecordingAsync called");
            await _source.StopRecordingAsync();
            Log.Info(Comp, "StopRecordingAsync: recorder stopped — restarting silent capture");

            RestartSilentAfterStop();
        }

        public void StopRecordingSync()
        {
            Log.Info(Comp, "StopRecordingSync called");
            _source.StopRecording();
            RestartSilentAfterStop();
        }

        private void RestartSilentAfterStop()
        {
            try
            {
                if (_silentCapture != null && _device != null)
                    _silentCapture.StartRecording();
            }
            catch (Exception ex)
            {
                Log.Warn(Comp, $"Failed to restart silent capture ({ex.Message}) — calling RestartSilentCapture");
                RestartSilentCapture();
            }
        }

        public void Dispose()
        {
            Log.Info(Comp, "Dispose called");
            DetachDevice();
            _source.Dispose();
        }

        public void RestartSilentCapture()
        {
            Log.Info(Comp, $"RestartSilentCapture called  IsRecording={IsRecording}  _device={(_device != null ? "EXISTS" : "NULL")}");

            if (IsRecording || _device == null)
            {
                Log.Warn(Comp, $"RestartSilentCapture SKIPPED reason={(IsRecording ? "IsRecording=true" : "_device=NULL")}");
                return;
            }

            try
            {
                _silentCapture?.StopRecording();
                _silentCapture?.Dispose();
                _silentCapture = null;

                _silentCapture = new WasapiCapture(_device, true, 50);
                _silentCapture.DataAvailable += SilentCapture_DataAvailable;
                _silentCapture.StartRecording();

                Log.Info(Comp, "RestartSilentCapture SUCCESS");
            }
            catch (Exception ex)
            {
                Log.Error(Comp, ex, "RestartSilentCapture FAILED");
            }
        }

        private void OnRecordingAborted(Exception ex)
        {
            const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);

            Log.Error(Comp, $"OnRecordingAborted  HRESULT=0x{ex.HResult:X8}  Message={ex.Message}");

            if (ex.HResult == AUDCLNT_E_DEVICE_INVALIDATED)
            {
                HandleDeviceFailure();
            }
            else
            {
                RestartSilentCapture();
            }

            RecordingAborted?.Invoke(ex);
        }
    }
}