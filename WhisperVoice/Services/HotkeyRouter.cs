using System;
using System.Windows.Input;

namespace WhisperVoice.Services
{
    public sealed class HotkeyRouter : IDisposable
    {
        private readonly IKeyboardHookProvider _provider;

        public event EventHandler<HotkeyRequestedEventArgs>? OnRecordRequested;
        public event EventHandler<HotkeyRequestedEventArgs>? OnRecordStopped;
        public event EventHandler? OnToggleMenu;
        public event EventHandler? OnOpenNotepad;

        private ProcessingMode _currentPttMode;
        private AudioSource _currentPttSource;

        public HotkeyRouter(IKeyboardHookProvider provider)
        {
            _provider = provider;
        }

        public void RebindHotkeys(AppSettings settings)
        {
            _provider.UnregisterAll();

            // 1. Always-on UI Hotkeys
            _provider.RegisterHotkey("ToggleMenu", settings.HotkeyMenu, false, 
                () => OnToggleMenu?.Invoke(this, EventArgs.Empty), null!);
            _provider.RegisterHotkey("OpenNotepad", settings.HotkeyNotepad, false, 
                () => OnOpenNotepad?.Invoke(this, EventArgs.Empty), null!);

            // 2. Matrix Registration
            if (settings.IsPushToTalkEnabled)
            {
                _provider.RegisterHotkey("PrimaryPTT", settings.HotkeyPrimary, true, 
                    () => HandlePttStart(ProcessingMode.Primary), () => HandlePttStop(ProcessingMode.Primary));
                _provider.RegisterHotkey("TranslatePTT", settings.HotkeyTranslate, true, 
                    () => HandlePttStart(ProcessingMode.Translate), () => HandlePttStop(ProcessingMode.Translate));
                _provider.RegisterHotkey("PromptPTT", settings.HotkeyPrompt, true, 
                    () => HandlePttStart(ProcessingMode.Prompt), () => HandlePttStop(ProcessingMode.Prompt));
            }
            else
            {
                // Toggle Mode
                _provider.RegisterHotkey("PrimaryMic", settings.HotkeyPrimary, false, 
                    () => HandleToggle(ProcessingMode.Primary, AudioSource.Microphone), null!);
                _provider.RegisterHotkey("PrimaryLoopback", "Ctrl+" + settings.HotkeyPrimary, false, 
                    () => HandleToggle(ProcessingMode.Primary, AudioSource.Loopback), null!);
                
                _provider.RegisterHotkey("TranslateMic", settings.HotkeyTranslate, false, 
                    () => HandleToggle(ProcessingMode.Translate, AudioSource.Microphone), null!);
                _provider.RegisterHotkey("TranslateLoopback", "Ctrl+" + settings.HotkeyTranslate, false, 
                    () => HandleToggle(ProcessingMode.Translate, AudioSource.Loopback), null!);
                
                _provider.RegisterHotkey("PromptMic", settings.HotkeyPrompt, false, 
                    () => HandleToggle(ProcessingMode.Prompt, AudioSource.Microphone), null!);
                _provider.RegisterHotkey("PromptLoopback", "Ctrl+" + settings.HotkeyPrompt, false, 
                    () => HandleToggle(ProcessingMode.Prompt, AudioSource.Loopback), null!);
            }
        }

        private void HandlePttStart(ProcessingMode mode)
        {
            _currentPttMode = mode;
            bool isCtrl = _provider.IsModifierKeyDown(ModifierKeys.Control);
            _currentPttSource = isCtrl ? AudioSource.Loopback : AudioSource.Microphone;
            OnRecordRequested?.Invoke(this, new HotkeyRequestedEventArgs(_currentPttMode, _currentPttSource));
        }

        private void HandlePttStop(ProcessingMode mode)
        {
            if (_currentPttMode == mode)
            {
                OnRecordStopped?.Invoke(this, new HotkeyRequestedEventArgs(_currentPttMode, _currentPttSource));
            }
        }

        private void HandleToggle(ProcessingMode mode, AudioSource source)
        {
            OnRecordRequested?.Invoke(this, new HotkeyRequestedEventArgs(mode, source));
        }

        public void Dispose() => _provider.Dispose();
    }
}
