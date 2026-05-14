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
    /// HOTKEY MATRIX:
    ///   F8              — Microphone, Primary
    ///   Ctrl+F8         — Loopback,   Primary
    ///   F9              — Microphone, Translate
    ///   Ctrl+F9         — Microphone, Translate with Prompt tags
    ///   F10             — Loopback,   Translate
    ///   Ctrl+F10        — Loopback,   Translate with Prompt tags
    ///
    /// TOGGLE MODE — CTRL CONFLICT FIX:
    ///   The root bug is that NHotkey calls RegisterHotKey with MOD_CONTROL for
    ///   "Ctrl+F8", but Windows dispatches WM_HOTKEY only when NO other modifier
    ///   (Alt, Shift, Win) is held.  When the base key (F8) is also registered
    ///   WITHOUT Ctrl, Windows matches the bare-key registration first and the
    ///   Ctrl variant never fires.
    ///   Fix: register Ctrl variants under SEPARATE NHotkey names with an
    ///   EXPLICIT ModifierKeys.Control flag so their WM_HOTKEY IDs are distinct.
    ///   The F10/Ctrl+F10 pair is hard-coded rather than derived from settings to
    ///   avoid any accidental collision with the primary/translate key bindings.
    ///
    /// PTT MODE — CTRL DETECTION FIX:
    ///   The LowLevelKeyboardHook fires on the bare key VK code (e.g., VK_F8).
    ///   At that instant we call GetAsyncKeyState(VK_CONTROL) to sample the
    ///   physical Ctrl state.  The result is passed to the callback delegate so
    ///   MainWindow can route to Loopback vs Microphone without any race.
    ///   We expose separate "with Ctrl" callbacks (onPttPrimaryLoopbackStart,
    ///   onPttTranslateLoopbackStart) so the hook can branch without knowing
    ///   about audio services.
    ///
    /// KEY-IDENTITY LOCK (PTT, Bug-1 fix):
    ///   Recording stops ONLY when key-up carries the identical scanCode that
    ///   started recording.  Chromium SendInput injections (scanCode = 0, exotic
    ///   vkCode) are rejected, preventing spurious PTT stops.
    /// </summary>
    public sealed class HotkeyOrchestrationService : IDisposable
    {
        // ── Win32 for Ctrl sampling inside the low-level hook ─────────────────
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private const int VK_CONTROL = 0x11;

        // ── Callbacks supplied by MainWindow ──────────────────────────────────

        // Toggle mode
        private readonly EventHandler<HotkeyEventArgs> _onRecordPrimary;
        private readonly EventHandler<HotkeyEventArgs> _onRecordTranslate;
        private readonly EventHandler<HotkeyEventArgs> _onRecordLoopbackPrimary;
        private readonly EventHandler<HotkeyEventArgs> _onRecordLoopbackTranslate;
        private readonly EventHandler<HotkeyEventArgs> _onTranslateCtrl;
        private readonly EventHandler<HotkeyEventArgs> _onToggleMenu;
        private readonly EventHandler<HotkeyEventArgs> _onOpenNotepad;

        // PTT mode — four distinct start callbacks so the hook never guesses intent
        private readonly Action _onPttPrimaryStart;              // F8 bare     → mic primary
        private readonly Action _onPttPrimaryLoopbackStart;      // Ctrl+F8     → loopback primary
        private readonly Action _onPttTranslateStart;            // F9 bare     → mic translate
        private readonly Action _onPttTranslateLoopbackStart;    // Ctrl+F9/F10 → loopback translate

        // Single shared stop — key release ends whichever session is active
        private readonly Action _onPttStop;

        // ── Low-level hook instances (PTT mode only) ──────────────────────────
        private LowLevelKeyboardHook? _primaryHook;
        private LowLevelKeyboardHook? _translateHook;

        // ── Constructor ───────────────────────────────────────────────────────

        /// <param name="onRecordPrimary">F8 toggle (mic, primary).</param>
        /// <param name="onRecordTranslate">F9 toggle (mic, translate).</param>
        /// <param name="onRecordLoopbackPrimary">Ctrl+F8 toggle (loopback, primary).</param>
        /// <param name="onRecordLoopbackTranslate">F10 / Ctrl+F10 toggle (loopback, translate).</param>
        /// <param name="onPttPrimaryStart">PTT key-down for mic primary (bare F8 without Ctrl).</param>
        /// <param name="onPttPrimaryLoopbackStart">PTT key-down for loopback primary (Ctrl+F8).</param>
        /// <param name="onPttTranslateStart">PTT key-down for mic translate (bare F9 without Ctrl).</param>
        /// <param name="onPttTranslateLoopbackStart">PTT key-down for loopback translate (Ctrl+F9 or F10).</param>
        /// <param name="onPttStop">PTT key-up — stops whichever session is active.</param>
        /// <param name="onToggleMenu">Hotkey to show/hide main window.</param>
        /// <param name="onTranslateCtrl">Ctrl+F9 toggle (mic, translate with prompts).</param>
        /// <param name="onOpenNotepad">Hotkey to show/hide notepad.</param>
        public HotkeyOrchestrationService(
            EventHandler<HotkeyEventArgs> onRecordPrimary,
            EventHandler<HotkeyEventArgs> onRecordTranslate,
            EventHandler<HotkeyEventArgs> onRecordLoopbackPrimary,
            EventHandler<HotkeyEventArgs> onRecordLoopbackTranslate,
            Action onPttPrimaryStart,
            Action onPttPrimaryLoopbackStart,
            Action onPttTranslateStart,
            Action onPttTranslateLoopbackStart,
            Action onPttStop,
            EventHandler<HotkeyEventArgs> onToggleMenu,
            EventHandler<HotkeyEventArgs> onTranslateCtrl,
            EventHandler<HotkeyEventArgs> onOpenNotepad)
        {
            _onRecordPrimary = onRecordPrimary;
            _onRecordTranslate = onRecordTranslate;
            _onRecordLoopbackPrimary = onRecordLoopbackPrimary;
            _onRecordLoopbackTranslate = onRecordLoopbackTranslate;
            _onPttPrimaryStart = onPttPrimaryStart;
            _onPttPrimaryLoopbackStart = onPttPrimaryLoopbackStart;
            _onPttTranslateStart = onPttTranslateStart;
            _onPttTranslateLoopbackStart = onPttTranslateLoopbackStart;
            _onPttStop = onPttStop;
            _onToggleMenu = onToggleMenu;
            _onTranslateCtrl = onTranslateCtrl;
            _onOpenNotepad = onOpenNotepad;
        }

        // ════════════════════════════════════════════════════════════════════════
        // Public API
        // ════════════════════════════════════════════════════════════════════════

        public void RebindHotkeys(AppSettings settings)
        {
            UnregisterAll();

            // Always-on global hotkeys (independent of PTT vs Toggle mode).
            TryRegister("ToggleMenu", settings.HotkeyMenu, _onToggleMenu);
            TryRegister("OpenNotepad", settings.HotkeyNotepad, _onOpenNotepad);

            var (primMods, primKey) = HotkeyParser.ParseNHotkey(settings.HotkeyPrimary);
            var (transMods, transKey) = HotkeyParser.ParseNHotkey(settings.HotkeyTranslate);

            if (settings.IsPushToTalkEnabled)
            {
                RegisterPttHooks(settings, primKey, transKey);
            }
            else
            {
                RegisterToggleHotkeys(settings, primMods, primKey, transMods, transKey);
            }
        }

        public void Dispose() => UnregisterAll();

        // ════════════════════════════════════════════════════════════════════════
        // Toggle mode registration
        // ════════════════════════════════════════════════════════════════════════

        private void RegisterToggleHotkeys(
            AppSettings settings,
            ModifierKeys primMods, Key primKey,
            ModifierKeys transMods, Key transKey)
        {
            // ── Bare microphone bindings (F8, F9) ─────────────────────────────
            // These must NOT include ModifierKeys.Control in their registration.
            // If Ctrl+F8 were also registered with the same NHotkey name, Windows
            // would merge them under a single WM_HOTKEY ID and the Ctrl variant
            // would never fire because the bare variant matches first.
            TryRegister("RecordPrimary", settings.HotkeyPrimary, _onRecordPrimary);
            TryRegister("RecordTranslate", settings.HotkeyTranslate, _onRecordTranslate);

            // ── Ctrl+Primary (e.g. Ctrl+F8) → Loopback Primary ───────────────
            // CRITICAL: use a SEPARATE name ("LoopbackPrimary") so NHotkey calls
            // RegisterHotKey with a distinct hotkey ID.  Using the same name as
            // "RecordPrimary" would overwrite it and both variants would break.
            if (primKey != Key.None)
            {
                // Force ModifierKeys.Control; strip any existing Ctrl from primMods
                // to avoid double-counting if the user typed "Ctrl+F8" in settings.
                var loopbackMods = (primMods & ~ModifierKeys.Control) | ModifierKeys.Control;
                TryRegisterExplicit("LoopbackPrimary", primKey, loopbackMods, _onRecordLoopbackPrimary);
            }

            // ── Ctrl+Translate (e.g. Ctrl+F9) → Mic Translate with Prompts ───
            if (transKey != Key.None)
            {
                var ctrlTransMods = (transMods & ~ModifierKeys.Control) | ModifierKeys.Control;
                TryRegisterExplicit("TranslateCtrl", transKey, ctrlTransMods, _onTranslateCtrl);
            }

            // ── F10 / Ctrl+F10 — dedicated loopback translate bindings ────────
            // These are hard-coded rather than derived from settings to ensure
            // they never collide with the configurable primary/translate keys.
            // F10 (bare) → Loopback Translate (original language)
            // Ctrl+F10   → Loopback Translate with Prompt tags
            TryRegisterExplicit("LoopbackF10", Key.F10, ModifierKeys.None, _onRecordLoopbackTranslate);
            TryRegisterExplicit("LoopbackF10Ctrl", Key.F10, ModifierKeys.Control, _onRecordLoopbackTranslate);
        }

        // ════════════════════════════════════════════════════════════════════════
        // PTT mode registration
        // ════════════════════════════════════════════════════════════════════════

        private void RegisterPttHooks(AppSettings settings, Key primKey, Key transKey)
        {
            var primaryVk = HotkeyParser.ParseVk(settings.HotkeyPrimary);
            var translateVk = HotkeyParser.ParseVk(settings.HotkeyTranslate);

            if (primaryVk.HasValue)
            {
                // The hook fires on the bare VK code (F8).  Inside the callback we
                // sample GetAsyncKeyState(VK_CONTROL) to decide which route to take:
                //   Ctrl held → onPttPrimaryLoopbackStart (loopback primary)
                //   Bare      → onPttPrimaryStart (mic primary)
                try
                {
                    _primaryHook = new LowLevelKeyboardHook(
                        primaryVk.Value,
                        onKeyDown: () =>
                        {
                            bool ctrlHeld = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                            if (ctrlHeld)
                                _onPttPrimaryLoopbackStart();
                            else
                                _onPttPrimaryStart();
                        },
                        onKeyUp: _onPttStop);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Hotkey] Failed to install primary PTT hook: {ex.Message}");
                }
            }

            if (translateVk.HasValue)
            {
                // F9 hook:
                //   Ctrl held → onPttTranslateLoopbackStart (loopback translate / with prompts)
                //   Bare      → onPttTranslateStart (mic translate)
                try
                {
                    _translateHook = new LowLevelKeyboardHook(
                        translateVk.Value,
                        onKeyDown: () =>
                        {
                            bool ctrlHeld = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                            if (ctrlHeld)
                                _onPttTranslateLoopbackStart();
                            else
                                _onPttTranslateStart();
                        },
                        onKeyUp: _onPttStop);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Hotkey] Failed to install translate PTT hook: {ex.Message}");
                }
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // Helpers
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Parses <paramref name="keyString"/> and registers it with NHotkey.
        /// Swallows registration failures and writes to Debug output.
        /// </summary>
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
                System.Diagnostics.Debug.WriteLine(
                    $"[Hotkey] Failed to register '{name}' ({keyString}): {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine(
                    $"[Hotkey] Failed to register explicit '{name}' ({mods}+{key}): {ex.Message}");
            }
        }

        private void UnregisterAll()
        {
            _primaryHook?.Dispose(); _primaryHook = null;
            _translateHook?.Dispose(); _translateHook = null;

            foreach (var name in new[]
            {
                "RecordPrimary", "RecordTranslate",
                "ToggleMenu", "OpenNotepad",
                "TranslateCtrl",
                "LoopbackPrimary",
                "LoopbackF10", "LoopbackF10Ctrl"
            })
            {
                try { HotkeyManager.Current.Remove(name); }
                catch { /* key was never registered — safe to ignore */ }
            }
        }
    }


    // ══════════════════════════════════════════════════════════════════════════
    // LowLevelKeyboardHook — WH_KEYBOARD_LL wrapper with key-identity lock
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Installs a WH_KEYBOARD_LL hook scoped to a single target virtual-key code.
    ///
    /// KEY-IDENTITY LOCK (Bug-1 fix):
    ///   A KBDLLHOOKSTRUCT carries both <c>vkCode</c> (virtual key) and
    ///   <c>scanCode</c> (hardware scan code). When recording starts (key-down),
    ///   the scan code is stored as the active identity. The "stop" callback is
    ///   invoked ONLY when a subsequent WM_KEYUP / WM_SYSKEYUP arrives with the
    ///   same scan-code identity.
    ///
    ///   Chromium injects multimedia keys via SendInput with scanCode = 0 and a
    ///   different vkCode. Neither matches a real F-key press, so they are silently
    ///   discarded and can never cause a spurious PTT stop.
    ///
    ///   Defence-in-depth: a secondary blocklist of known multimedia VK codes is
    ///   checked before the identity lock, providing redundancy.
    /// </summary>
    internal sealed class LowLevelKeyboardHook : IDisposable
    {
        // ── Win32 P/Invoke ────────────────────────────────────────────────────
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
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        // ── Defence-in-depth: known multimedia / volume VK codes ─────────────
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

        private readonly uint _targetVk;
        private readonly Action _onKeyDown;
        private readonly Action _onKeyUp;

        private volatile uint _activeScanCode = 0;
        private readonly LowLevelKeyboardProc _proc;  // pinned delegate — must not be collected
        private readonly IntPtr _hookHandle;

        private bool _disposed;

        internal LowLevelKeyboardHook(uint targetVk, Action onKeyDown, Action onKeyUp)
        {
            _targetVk = targetVk;
            _onKeyDown = onKeyDown;
            _onKeyUp = onKeyUp;
            _proc = HookCallback;

            using var process = System.Diagnostics.Process.GetCurrentProcess();
            using var module = process.MainModule
                ?? throw new InvalidOperationException("Cannot get main module handle.");

            _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName!), 0);

            if (_hookHandle == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"SetWindowsHookEx failed with Win32 error {Marshal.GetLastWin32Error()}");

            System.Diagnostics.Debug.WriteLine(
                $"[LowLevelKeyboardHook] Installed hook for VK=0x{_targetVk:X2}");
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // Guard 1: blocklist — drop known multimedia / volume injections.
            foreach (var blocked in _blockedVkCodes)
            {
                if (kbd.vkCode == blocked)
                    return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            // Guard 2: only act on our target VK code.
            if (kbd.vkCode != _targetVk)
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            if (isDown)
            {
                // Ignore key-repeat events (key held down).
                if (_activeScanCode != 0)
                    return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

                // Store the hardware identity for this press.
                _activeScanCode = kbd.scanCode != 0 ? kbd.scanCode : kbd.vkCode;

                System.Diagnostics.Debug.WriteLine(
                    $"[LowLevelKeyboardHook] KeyDown VK=0x{kbd.vkCode:X2} SC=0x{kbd.scanCode:X2} " +
                    $"→ PTT START (identity={_activeScanCode})");

                // onKeyDown was injected by HotkeyOrchestrationService and already
                // samples GetAsyncKeyState for Ctrl, so no modifier logic here.
                _onKeyDown.Invoke();
            }
            else if (isUp)
            {
                uint expected = _activeScanCode;
                if (expected == 0)
                    return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

                uint incoming = kbd.scanCode != 0 ? kbd.scanCode : kbd.vkCode;

                if (incoming != expected)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[LowLevelKeyboardHook] KeyUp VK=0x{kbd.vkCode:X2} SC=0x{kbd.scanCode:X2} " +
                        $"REJECTED (expected={expected}, got={incoming}) — injected event dropped");
                    return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[LowLevelKeyboardHook] KeyUp VK=0x{kbd.vkCode:X2} SC=0x{kbd.scanCode:X2} → PTT STOP");

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
    // HotkeyParser — converts "F8" / "Ctrl+F9" strings to VK / WPF Key
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

            var parts = keyString.Split('+');
            string bare = parts[^1].Trim();

            // F1–F24 fast path.
            if (bare.Length >= 2 && bare[0] is 'F' or 'f' &&
                int.TryParse(bare[1..], out int fn) && fn is >= 1 and <= 24)
            {
                return (uint)(0x6F + fn); // VK_F1 = 0x70
            }

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
