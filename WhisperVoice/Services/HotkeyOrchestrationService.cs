using NHotkey;
using NHotkey.Wpf;
using System;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace WhisperVoice.Services
{
    /// <summary>
    /// Owns two independent hotkey strategies and switches between them based on
    /// <see cref="AppSettings.IsPushToTalkEnabled"/>:
    ///   • Toggle mode — NHotkey (RegisterHotKey Win32 API).
    ///   • PTT mode    — WH_KEYBOARD_LL low-level hook with key-identity locking.
    ///
    /// BUG-1 FIX — Key-Identity Lock:
    ///   The PTT "stop" callback fires ONLY when the key-up event carries the exact
    ///   same vkCode + scanCode pair that initiated recording. Chromium multimedia-key
    ///   injections (VK_MEDIA_PLAY_PAUSE etc.) arrive with a different vkCode and/or
    ///   scanCode = 0, so they are silently dropped and can never trigger a spurious
    ///   recording stop.
    /// </summary>
    public sealed class HotkeyOrchestrationService : IDisposable
    {
        // ── Callbacks supplied by MainWindow ──────────────────────────────────
        private readonly EventHandler<HotkeyEventArgs> _onRecordPrimary;
        private readonly EventHandler<HotkeyEventArgs> _onRecordTranslate;
        private readonly Action _onPttPrimaryStart;
        private readonly Action _onPttPrimaryStop;
        private readonly Action _onPttTranslateStart;
        private readonly Action _onPttTranslateStop;
        private readonly EventHandler<HotkeyEventArgs> _onToggleMenu;
        private readonly EventHandler<HotkeyEventArgs> _onTranslateCtrl;
        private readonly EventHandler<HotkeyEventArgs> _onOpenNotepad;

        // ── Low-level hook instances (PTT mode only) ──────────────────────────
        private LowLevelKeyboardHook? _primaryHook;
        private LowLevelKeyboardHook? _translateHook;

        public HotkeyOrchestrationService(
            EventHandler<HotkeyEventArgs> onRecordPrimary,
            EventHandler<HotkeyEventArgs> onRecordTranslate,
            Action onPttPrimaryStart,
            Action onPttPrimaryStop,
            Action onPttTranslateStart,
            Action onPttTranslateStop,
            EventHandler<HotkeyEventArgs> onToggleMenu,
            EventHandler<HotkeyEventArgs> onTranslateCtrl,
            EventHandler<HotkeyEventArgs> onOpenNotepad)
        {
            _onRecordPrimary    = onRecordPrimary;
            _onRecordTranslate  = onRecordTranslate;
            _onPttPrimaryStart  = onPttPrimaryStart;
            _onPttPrimaryStop   = onPttPrimaryStop;
            _onPttTranslateStart = onPttTranslateStart;
            _onPttTranslateStop = onPttTranslateStop;
            _onToggleMenu       = onToggleMenu;
            _onTranslateCtrl    = onTranslateCtrl;
            _onOpenNotepad      = onOpenNotepad;
        }

        // ════════════════════════════════════════════════════════════════════════
        // Public API
        // ════════════════════════════════════════════════════════════════════════

        public void RebindHotkeys(AppSettings settings)
        {
            UnregisterAll();

            // Always-on toggle hotkeys (NHotkey / RegisterHotKey)
            TryRegister("ToggleMenu",    settings.HotkeyMenu,    _onToggleMenu);
            TryRegister("OpenNotepad",   settings.HotkeyNotepad, _onOpenNotepad);
            TryRegister("TranslateCtrl", "Ctrl+F9",              _onTranslateCtrl);

            if (settings.IsPushToTalkEnabled)
            {
                // PTT mode: low-level hook with key-identity lock (Bug-1 fix)
                var primaryVk   = HotkeyParser.ParseVk(settings.HotkeyPrimary);
                var translateVk = HotkeyParser.ParseVk(settings.HotkeyTranslate);

                if (primaryVk.HasValue)
                {
                    try
                    {
                        _primaryHook = new LowLevelKeyboardHook(
                            primaryVk.Value,
                            onKeyDown: _onPttPrimaryStart,
                            onKeyUp:   _onPttPrimaryStop);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[Hotkey] Failed to install primary PTT hook: {ex.Message}");
                    }
                }

                if (translateVk.HasValue)
                {
                    try
                    {
                        _translateHook = new LowLevelKeyboardHook(
                            translateVk.Value,
                            onKeyDown: _onPttTranslateStart,
                            onKeyUp:   _onPttTranslateStop);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[Hotkey] Failed to install translate PTT hook: {ex.Message}");
                    }
                }
            }
            else
            {
                // Toggle mode: NHotkey
                TryRegister("RecordPrimary",   settings.HotkeyPrimary,   _onRecordPrimary);
                TryRegister("RecordTranslate", settings.HotkeyTranslate, _onRecordTranslate);
            }
        }

        public void Dispose() => UnregisterAll();

        // ════════════════════════════════════════════════════════════════════════
        // Helpers
        // ════════════════════════════════════════════════════════════════════════

        private static void TryRegister(string name, string keyString,
                                        EventHandler<HotkeyEventArgs> handler)
        {
            try
            {
                var (mods, key) = HotkeyParser.ParseNHotkey(keyString);
                HotkeyManager.Current.AddOrReplace(name, key, mods, handler);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Hotkey] Failed to register '{name}' ({keyString}): {ex.Message}");
            }
        }

        private void UnregisterAll()
        {
            _primaryHook?.Dispose();   _primaryHook   = null;
            _translateHook?.Dispose(); _translateHook = null;

            foreach (var name in new[]
            {
                "RecordPrimary", "RecordTranslate",
                "ToggleMenu", "OpenNotepad", "TranslateCtrl"
            })
            {
                try { HotkeyManager.Current.Remove(name); } catch { }
            }
        }
    }


    // ══════════════════════════════════════════════════════════════════════════
    // LowLevelKeyboardHook — core of Bug-1 fix
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Installs a WH_KEYBOARD_LL hook scoped to a single target virtual-key code.
    ///
    /// BUG-1 FIX — Key-Identity Lock:
    ///   A KBDLLHOOKSTRUCT carries both <c>vkCode</c> (virtual key) and
    ///   <c>scanCode</c> (hardware scan code). When recording starts (key-down),
    ///   both values are stored in <c>_activeScanCode</c>. The "stop" callback is
    ///   invoked ONLY when a subsequent WM_KEYUP / WM_SYSKEYUP arrives with the
    ///   identical scan-code identity.
    ///
    ///   Chromium injects multimedia keys via SendInput with scanCode = 0 and a
    ///   different vkCode. Neither matches a real F-key press, so they are silently
    ///   discarded and can never cause a spurious PTT stop.
    ///
    ///   Defence-in-depth: a secondary blocklist of known multimedia VK codes
    ///   is checked before the identity lock, providing redundancy.
    /// </summary>
    internal sealed class LowLevelKeyboardHook : IDisposable
    {
        // ── Win32 P/Invoke ────────────────────────────────────────────────────
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook,
            LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
            IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint   vkCode;
            public uint   scanCode;
            public uint   flags;
            public uint   time;
            public IntPtr dwExtraInfo;
        }

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN     = 0x0100;
        private const int WM_KEYUP       = 0x0101;
        private const int WM_SYSKEYDOWN  = 0x0104;
        private const int WM_SYSKEYUP   = 0x0105;

        // ── Defence-in-depth: known multimedia / volume VK codes ─────────────
        // Primary guard is the scan-code identity check; this blocklist is
        // redundant but makes intent explicit and is essentially zero-cost.
        private static readonly uint[] _blockedVkCodes =
        {
            0xAD, // VK_VOLUME_MUTE
            0xAE, // VK_VOLUME_DOWN
            0xAF, // VK_VOLUME_UP
            0xB0, // VK_MEDIA_NEXT_TRACK
            0xB1, // VK_MEDIA_PREV_TRACK
            0xB2, // VK_MEDIA_STOP
            0xB3, // VK_MEDIA_PLAY_PAUSE
            0xB4, // VK_LAUNCH_MAIL
            0xB5, // VK_LAUNCH_MEDIA_SELECT
        };

        // ── Instance state ────────────────────────────────────────────────────
        private readonly uint   _targetVk;
        private readonly Action _onKeyDown;
        private readonly Action _onKeyUp;

        /// <summary>
        /// The scan-code (or vkCode fallback) of the key-down that started the
        /// current PTT session. 0 = no active press.
        /// Set on key-down, cleared on matching key-up.
        /// </summary>
        private volatile uint _activeScanCode = 0;

        // Delegate field prevents the GC from collecting the callback while the hook is live.
        private readonly LowLevelKeyboardProc _proc;
        private readonly IntPtr _hookHandle;

        private bool _disposed;

        internal LowLevelKeyboardHook(uint targetVk, Action onKeyDown, Action onKeyUp)
        {
            _targetVk  = targetVk;
            _onKeyDown = onKeyDown;
            _onKeyUp   = onKeyUp;
            _proc      = HookCallback; // pin delegate

            using var process = System.Diagnostics.Process.GetCurrentProcess();
            using var module  = process.MainModule
                ?? throw new InvalidOperationException("Cannot get main module handle.");

            _hookHandle = SetWindowsHookEx(
                WH_KEYBOARD_LL, _proc,
                GetModuleHandle(module.ModuleName!), 0);

            if (_hookHandle == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"SetWindowsHookEx failed with Win32 error {Marshal.GetLastWin32Error()}");

            System.Diagnostics.Debug.WriteLine(
                $"[LowLevelKeyboardHook] Installed hook for VK=0x{_targetVk:X2}");
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // ── Guard 1: blocklist — drop known multimedia / volume injections ──
            foreach (var blocked in _blockedVkCodes)
            {
                if (kbd.vkCode == blocked)
                    return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            // ── Guard 2: only act on our target VK code ────────────────────────
            if (kbd.vkCode != _targetVk)
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            int msg     = wParam.ToInt32();
            bool isDown = msg == WM_KEYDOWN  || msg == WM_SYSKEYDOWN;
            bool isUp   = msg == WM_KEYUP    || msg == WM_SYSKEYUP;

            if (isDown)
            {
                // Auto-repeat guard: if _activeScanCode is already set, the key is
                // being held — do not fire onKeyDown again.
                if (_activeScanCode != 0)
                    return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

                // Use scanCode as the identity token. For real physical key presses
                // scanCode is always non-zero. Fall back to vkCode only for rare
                // soft-keyboard drivers that report scanCode = 0.
                _activeScanCode = kbd.scanCode != 0 ? kbd.scanCode : kbd.vkCode;

                System.Diagnostics.Debug.WriteLine(
                    $"[LowLevelKeyboardHook] KeyDown VK=0x{kbd.vkCode:X2} " +
                    $"SC=0x{kbd.scanCode:X2} → PTT START (identity={_activeScanCode})");

                _onKeyDown.Invoke();
            }
            else if (isUp)
            {
                // ── KEY-IDENTITY LOCK ─────────────────────────────────────────────
                // Only release if the incoming scan code matches what started recording.
                // scanCode == 0 is the canonical tell-tale of a software injection
                // (SendInput with KEYEVENTF_SCANCODE not set), which Chromium uses
                // for its media-key events. Such events will never match a real keystroke.
                uint expectedScan = _activeScanCode;

                if (expectedScan == 0)
                {
                    // No active press — ignore stray key-up events.
                    return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                }

                uint incomingScan = kbd.scanCode != 0 ? kbd.scanCode : kbd.vkCode;

                if (incomingScan != expectedScan)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[LowLevelKeyboardHook] KeyUp VK=0x{kbd.vkCode:X2} " +
                        $"SC=0x{kbd.scanCode:X2} REJECTED " +
                        $"(expected identity={expectedScan}, got {incomingScan}) — injected event dropped");
                    return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[LowLevelKeyboardHook] KeyUp VK=0x{kbd.vkCode:X2} " +
                    $"SC=0x{kbd.scanCode:X2} → PTT STOP");

                _activeScanCode = 0;
                _onKeyUp.Invoke();
            }

            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_hookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookHandle);
                System.Diagnostics.Debug.WriteLine(
                    $"[LowLevelKeyboardHook] Unhooked VK=0x{_targetVk:X2}");
            }
        }
    }


    // ══════════════════════════════════════════════════════════════════════════
    // HotkeyParser  — converts "F8" / "Ctrl+F9" strings to VK / WPF Key
    // ══════════════════════════════════════════════════════════════════════════

    internal static class HotkeyParser
    {
        /// <summary>
        /// Extracts the bare virtual-key code from a hotkey string like "F8" or "Ctrl+F8".
        /// Returns null if the string cannot be parsed.
        /// </summary>
        public static uint? ParseVk(string keyString)
        {
            if (string.IsNullOrWhiteSpace(keyString)) return null;

            // Strip modifier prefixes; we only need the bare key for the low-level hook.
            var parts = keyString.Split('+');
            string bare = parts[^1].Trim();

            // F1–F24 fast path
            if (bare.Length >= 2 && bare[0] is 'F' or 'f' &&
                int.TryParse(bare[1..], out int fn) && fn is >= 1 and <= 24)
            {
                return (uint)(0x6F + fn); // VK_F1 = 0x70
            }

            // Fall back to WPF KeyInterop
            if (Enum.TryParse<Key>(bare, ignoreCase: true, out var wpfKey))
            {
                int vk = KeyInterop.VirtualKeyFromKey(wpfKey);
                return vk > 0 ? (uint)vk : null;
            }

            return null;
        }

        /// <summary>
        /// Parses a hotkey string like "Ctrl+F8" into (ModifierKeys, Key) for NHotkey.
        /// </summary>
        public static (ModifierKeys mods, Key key) ParseNHotkey(string s)
        {
            var mods  = ModifierKeys.None;
            var parts = s.Split('+');
            Key key   = Key.None;

            foreach (var part in parts)
            {
                switch (part.Trim().ToUpperInvariant())
                {
                    case "CTRL":
                    case "CONTROL": mods |= ModifierKeys.Control; break;
                    case "ALT":     mods |= ModifierKeys.Alt;     break;
                    case "SHIFT":   mods |= ModifierKeys.Shift;   break;
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
