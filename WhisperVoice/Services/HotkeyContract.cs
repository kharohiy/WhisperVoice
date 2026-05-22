using System;

namespace WhisperVoice.Services
{
    public enum AudioSource { Microphone, Loopback }
    public enum ProcessingMode { Primary, Translate, Prompt }

    public class HotkeyRequestedEventArgs : EventArgs
    {
        public ProcessingMode Mode { get; }
        public AudioSource Source { get; }

        public HotkeyRequestedEventArgs(ProcessingMode mode, AudioSource source)
        {
            Mode = mode;
            Source = source;
        }
    }
}
