using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;

namespace WhisperVoice
{
    public class AudioRecorder
    {
        private WasapiCapture? capture;
        private WaveFileWriter? writer;
        public bool IsRecording { get; private set; }

        public void StartRecording(string deviceId, string filePath)
        {
            try
            {
                var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                var device = enumerator.GetDevice(deviceId);

                capture = new WasapiCapture(device);
                capture.WaveFormat = new WaveFormat(16000, 1);
                writer = new WaveFileWriter(filePath, capture.WaveFormat);

                capture.DataAvailable += (s, a) => {
                    if (a.BytesRecorded > 0 && writer != null)
                        writer.Write(a.Buffer, 0, a.BytesRecorded);
                };

                capture.RecordingStopped += (s, a) => {
                    writer?.Dispose(); writer = null;
                    capture?.Dispose(); capture = null;
                };

                capture.StartRecording();
                IsRecording = true;
            }
            catch { }
        }

        public void StopRecording() { capture?.StopRecording(); IsRecording = false; }
    }
}