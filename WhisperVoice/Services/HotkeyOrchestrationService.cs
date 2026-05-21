using NHotkey;
using NHotkey.Wpf;
using System;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace WhisperVoice.Services
{
    // ── Contract Architecture ───────────────────────────────────────────
    
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

    // ── Orchestration Service ───────────────────────────────────────────

    /// <summary>
    /// Event-driven hotkey orchestration. Exposes unified events for MainWindow.
    /// Supports the Hotkey Matrix (Key = Mic, Ctrl+Key = Loopback) across both
    /// Toggle and Push-to-Talk modes.
    /// </summary>
    public sealed class HotkeyOrchestrationService : IDisposable
    {
        public event EventHandler<HotkeyRequestedEventArgs>? OnRecordRequested;
        public event EventHandler<HotkeyRequestedEventArgs>? OnRecordStopped;
        public event EventHandler? OnToggleMenu;
        public event EventHandler? OnOpenNotepad;

        private LowLevelKeyboardHook? _primaryHook;
        private LowLevelKeyboardHook? _translateHook;
        private LowLevelKeyboardHook? _promptHook;

        private ProcessingMode _currentPttMode;
        private AudioSource _currentPttSource;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private const int VK_CONTROL = 0x11;

        public HotkeyOrchestrationService()
        {
        }

        public void RebindHotkeys(AppSettings settings)
        {
            UnregisterAll();

            // 1. Always-on UI Hotkeys
            TryRegister("ToggleMenu", settings.HotkeyMenu, (_, _) => OnToggleMenu?.Invoke(this, EventArgs.Empty));
            TryRegister("OpenNotepad", settings.HotkeyNotepad, (_, _) => OnOpenNotepad?.Invoke(this, EventArgs.Empty));

            // 2. Matrix Registration
            if (settings.IsPushToTalkEnabled)
            {
                // PTT Mode: Hooks for base keys. Modifier (Ctrl) is evaluated dynamically at press.
                var primaryVk   = HotkeyParser.ParseVk(settings.HotkeyPrimary);
                var translateVk = HotkeyParser.ParseVk(settings.HotkeyTranslate);
                var promptVk    = HotkeyParser.ParseVk(settings.HotkeyPrompt);

                if (primaryVk.HasValue)
                    _primaryHook = SafeCreateHook(primaryVk.Value, ProcessingMode.Primary);
                
                if (translateVk.HasValue)
                    _translateHook = SafeCreateHook(translateVk.Value, ProcessingMode.Translate);

                if (promptVk.HasValue)
                    _promptHook = SafeCreateHook(promptVk.Value, ProcessingMode.Prompt);
            }
            else
            {
                // Toggle Mode: Explicit NHotkey slots for every matrix combination
                // Primary
                TryRegister("PrimaryMic", settings.HotkeyPrimary, (_, _) => HandleToggle(ProcessingMode.Primary, AudioSource.Microphone));
                TryRegister("PrimaryLoopback", "Ctrl+" + settings.HotkeyPrimary, (_, _) => HandleToggle(ProcessingMode.Primary, AudioSource.Loopback));
                
                // Translate
                TryRegister("TranslateMic", settings.HotkeyTranslate, (_, _) => HandleToggle(ProcessingMode.Translate, AudioSource.Microphone));
                TryRegister("TranslateLoopback", "Ctrl+" + settings.HotkeyTranslate, (_, _) => HandleToggle(ProcessingMode.Translate, AudioSource.Loopback));
                
                // Prompt
                TryRegister("PromptMic", settings.HotkeyPrompt, (_, _) => HandleToggle(ProcessingMode.Prompt, AudioSource.Microphone));
                TryRegister("PromptLoopback", "Ctrl+" + settings.HotkeyPrompt, (_, _) => HandleToggle(ProcessingMode.Prompt, AudioSource.Loopback));
            }
        }

        private LowLevelKeyboardHook? SafeCreateHook(uint vk, ProcessingMode mode)
        {
            try
            {
                return new LowLevelKeyboardHook(
                    vk,
                    onKeyDown: () => HandlePttStart(mode),
                    onKeyUp:   () => HandlePttStop(mode));
            }
            catch (Exception ex)
            {
                WhisperVoice.DiagnosticLogger.Instance.Error("HotkeyOrchestrationService", ex, $"Failed to install PTT hook for VK {vk}");
                return null;
            }
        }

        private AudioSource EvaluateCurrentSource()
        {
            bool isCtrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            return isCtrlDown 
                ? AudioSource.Loopback 
                : AudioSource.Microphone;
        }

        private void HandlePttStart(ProcessingMode mode)
        {
            _currentPttMode = mode;
            _currentPttSource = EvaluateCurrentSource();
            OnRecordRequested?.Invoke(this, new HotkeyRequestedEventArgs(_currentPttMode, _currentPttSource));
        }

        private void HandlePttStop(ProcessingMode mode)
        {
            // Ensure we only fire stop for the actively held key
            if (_currentPttMode == mode)
            {
                OnRecordStopped?.Invoke(this, new HotkeyRequestedEventArgs(_currentPttMode, _currentPttSource));
            }
        }

        private void HandleToggle(ProcessingMode mode, AudioSource source)
        {
            OnRecordRequested?.Invoke(this, new HotkeyRequestedEventArgs(mode, source));
        }

        public void Dispose() => UnregisterAll();

        private static void TryRegister(string name, string keyString, EventHandler<HotkeyEventArgs> handler)
        {
            try
            {
                var (mods, key) = HotkeyParser.ParseNHotkey(keyString);
                if (key == Key.None) return;
                HotkeyManager.Current.AddOrReplace(name, key, mods, handler);
            }
            catch (Exception ex)
            {
                WhisperVoice.DiagnosticLogger.Instance.Error("HotkeyOrchestrationService", ex, $"Failed to register '{name}' ({keyString})");
            }
        }

        /// <summary>
        /// Registers a hotkey with an explicitly provided modifier set.
        /// Use this for Ctrl variants to guarantee a distinct WM_HOTKEY ID.
        /// </summary>
        private static void TryRegisterExplicit(string name, Key key, ModifierKeys mods, EventHandler<HotkeyEventArgs> handler)
        {
            try
            {
                if (key == Key.None) return;
                HotkeyManager.Current.AddOrReplace(name, key, mods, handler);
            }
            catch (Exception ex)
            {
                WhisperVoice.DiagnosticLogger.Instance.Error("HotkeyOrchestrationService", ex, $"Failed to register explicit '{name}' ({mods}+{key})");
            }
        }

        private void UnregisterAll()
        {
            _primaryHook?.Dispose(); _primaryHook = null;
            _translateHook?.Dispose(); _translateHook = null;
            _promptHook?.Dispose();    _promptHook    = null;

            var names = new[]
            {
                "ToggleMenu", "OpenNotepad",
                "PrimaryMic", "PrimaryLoopback",
                "TranslateMic", "TranslateLoopback",
                "PromptMic", "PromptLoopback"
            };

            foreach (var name in names)
            {
                try { HotkeyManager.Current.Remove(name); }
                catch { /* key was never registered — safe to ignore */ }
            }
        }
    }


    // ══════════════════════════════════════════════════════════════════════════
    // LowLevelKeyboardHook — WH_KEYBOARD_LL wrapper with key-identity lock
    // ══════════════════════════════════════════════════════════════════════════

    internal sealed class LowLevelKeyboardHook : IDisposable
    {
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN     = 0x0100;
        private const int WM_KEYUP       = 0x0101;
        private const int WM_SYSKEYDOWN  = 0x0104;
        private const int WM_SYSKEYUP    = 0x0105;

        private static readonly uint[] _blockedVkCodes =
        {
            0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5
        };

        private readonly uint   _targetVk;
        private readonly Action _onKeyDown;
        private readonly Action _onKeyUp;

        private volatile uint _activeScanCode = 0;

        private readonly LowLevelKeyboardProc _proc;
        private readonly IntPtr _hookHandle;

        private bool _disposed;

        internal LowLevelKeyboardHook(uint targetVk, Action onKeyDown, Action onKeyUp)
        {
            _targetVk = targetVk;
            _onKeyDown = onKeyDown;
            _onKeyUp   = onKeyUp;
            _proc      = HookCallback;

            using var process = System.Diagnostics.Process.GetCurrentProcess();
            using var module  = process.MainModule ?? throw new InvalidOperationException("Cannot get main module handle.");

            _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName!), 0);

            if (_hookHandle == IntPtr.Zero)
                throw new InvalidOperationException($"SetWindowsHookEx failed with Win32 error {Marshal.GetLastWin32Error()}");
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            foreach (var blocked in _blockedVkCodes)
            {
                if (kbd.vkCode == blocked) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            if (kbd.vkCode != _targetVk)
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            if (isDown)
            {
                if (_activeScanCode != 0) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

                _activeScanCode = kbd.scanCode != 0 ? kbd.scanCode : kbd.vkCode;
                _onKeyDown.Invoke();
            }
            else if (isUp)
            {
                uint expectedScan = _activeScanCode;
                if (expectedScan == 0) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

                uint incomingScan = kbd.scanCode != 0 ? kbd.scanCode : kbd.vkCode;
                if (incomingScan != expectedScan) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

                _activeScanCode = 0;
                _onKeyUp.Invoke();
            }

            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_hookHandle != IntPtr.Zero) UnhookWindowsHookEx(_hookHandle);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // HotkeyParser
    // ══════════════════════════════════════════════════════════════════════════

    internal static class HotkeyParser
    {
        public static uint? ParseVk(string keyString)
        {
            if (string.IsNullOrWhiteSpace(keyString)) return null;

            var parts = keyString.Split('+');
            string bare = parts[^1].Trim();

            if (bare.Length >= 2 && bare[0] is 'F' or 'f' &&
                int.TryParse(bare[1..], out int fn) && fn is >= 1 and <= 24)
            {
                return (uint)(0x6F + fn); 
            }

            if (Enum.TryParse<Key>(bare, ignoreCase: true, out var wpfKey))
            {
                int vk = KeyInterop.VirtualKeyFromKey(wpfKey);
                return vk > 0 ? (uint)vk : null;
            }

            return null;
        }

        public static (ModifierKeys mods, Key key) ParseNHotkey(string s)
        {
            var mods = ModifierKeys.None;
            var parts = s.Split('+');
            Key key = Key.None;

            foreach (var part in parts)
            {
                switch (part.Trim().ToUpperInvariant())
                {
                    case "CTRL":
                    case "CONTROL": mods |= ModifierKeys.Control; break;
                    case "ALT": mods |= ModifierKeys.Alt; break;
                    case "SHIFT": mods |= ModifierKeys.Shift; break;
                    case "WIN":
                    case "WINDOWS": mods |= ModifierKeys.Windows; break;
                    default:
                        Enum.TryParse(part.Trim(), ignoreCase: true, out key);
                        break;
                }
            }

            return (mods, key);
        }
    }
}