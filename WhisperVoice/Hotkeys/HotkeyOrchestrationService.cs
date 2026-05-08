using NHotkey;
using NHotkey.Wpf;
using System;
using System.Windows.Forms;
using System.Windows.Input;
using WpfKey = System.Windows.Input.Key;

namespace WhisperVoice.Hotkeys
{
    /// <summary>
    /// Owns all hotkey registration and the exclusive switch between Toggle mode
    /// (NHotkey / RegisterHotKey) and Push-to-Talk mode (WH_KEYBOARD_LL hook).
    ///
    /// Rules enforced here:
    ///   • Toggle mode and PTT mode NEVER run simultaneously for Primary/Translate keys.
    ///   • RebindHotkeys() is the single entry-point; call it on startup and after
    ///     every settings save.
    ///   • The three always-on toggle keys (ToggleMenu, TranslateCtrl, Notepad) are
    ///     registered via NHotkey in BOTH modes and are never touched by PTT logic.
    ///   • Dispose() tears down whichever mechanism is currently active.
    /// </summary>
    internal sealed class HotkeyOrchestrationService : IDisposable
    {
        // ── Injected handlers ──────────────────────────────────────────────
        // Toggle-mode handlers (wired to existing MainWindow methods)
        private readonly EventHandler<HotkeyEventArgs> _onRecordPrimary;
        private readonly EventHandler<HotkeyEventArgs> _onRecordTranslate;

        // PTT-mode handlers — separate per key so each hook fires the right mode
        private readonly Action _onPttPrimaryStart;
        private readonly Action _onPttPrimaryStop;
        private readonly Action _onPttTranslateStart;
        private readonly Action _onPttTranslateStop;

        // Always-on toggle handlers (mode-independent)
        private readonly EventHandler<HotkeyEventArgs> _onToggleMenu;
        private readonly EventHandler<HotkeyEventArgs> _onTranslateCtrl;
        private readonly EventHandler<HotkeyEventArgs> _onOpenNotepad;

        // ── Live state ─────────────────────────────────────────────────────
        private LowLevelKeyboardHook? _pttPrimaryHook;
        private LowLevelKeyboardHook? _pttTranslateHook;
        private bool _pttActive;
        private bool _disposed;

        // ── Constructor ────────────────────────────────────────────────────

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
            _onRecordPrimary   = onRecordPrimary;
            _onRecordTranslate = onRecordTranslate;
            _onPttPrimaryStart   = onPttPrimaryStart;
            _onPttPrimaryStop    = onPttPrimaryStop;
            _onPttTranslateStart = onPttTranslateStart;
            _onPttTranslateStop  = onPttTranslateStop;
            _onToggleMenu      = onToggleMenu;
            _onTranslateCtrl   = onTranslateCtrl;
            _onOpenNotepad     = onOpenNotepad;
        }

        // ══════════════════════════════════════════════════════════════════
        // Public API
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Tears down the currently active mechanism and registers the correct one
        /// based on the current AppSettings. Safe to call multiple times.
        /// Always call on the UI thread (NHotkey requires it).
        /// </summary>
        public void RebindHotkeys(AppSettings settings)
        {
            // ── Step 1: Unconditionally tear down previous state ───────────
            TearDownPtt();
            TearDownTogglePrimaryTranslate();

            // ── Step 2: Parse hotkey strings from settings ─────────────────
            if (!TryParseWpfKey(settings.HotkeyPrimary,   out WpfKey wpfPrimary))   return;
            if (!TryParseWpfKey(settings.HotkeyTranslate, out WpfKey wpfTranslate)) return;

            // ── Step 3: Always-on keys — registered in every mode ──────────
            RegisterAlwaysOnHotkeys();

            // ── Step 4: Mode-specific registration ─────────────────────────
            if (settings.IsPushToTalkEnabled)
            {
                RegisterPtt(wpfPrimary, wpfTranslate);
            }
            else
            {
                RegisterToggle(wpfPrimary, wpfTranslate);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Private: always-on hotkeys (mode-independent)
        // ══════════════════════════════════════════════════════════════════

        private void RegisterAlwaysOnHotkeys()
        {
            try
            {
                HotkeyManager.Current.AddOrReplace(
                    "ToggleMenu", WpfKey.F7, ModifierKeys.None, _onToggleMenu);
                HotkeyManager.Current.AddOrReplace(
                    "TranslateCtrl", WpfKey.F9, ModifierKeys.Control, _onTranslateCtrl);
                HotkeyManager.Current.AddOrReplace(
                    "Notepad", WpfKey.F7, ModifierKeys.Control, _onOpenNotepad);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[HotkeyOrchestration] Always-on hotkey registration failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Private: Toggle mode (NHotkey)
        // ══════════════════════════════════════════════════════════════════

        private void RegisterToggle(WpfKey primary, WpfKey translate)
        {
            try
            {
                HotkeyManager.Current.AddOrReplace(
                    "Primary",   primary,   ModifierKeys.None, _onRecordPrimary);
                HotkeyManager.Current.AddOrReplace(
                    "Translate", translate, ModifierKeys.None, _onRecordTranslate);

                _pttActive = false;
                System.Diagnostics.Debug.WriteLine(
                    $"[HotkeyOrchestration] Toggle mode registered: Primary={primary}, Translate={translate}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[HotkeyOrchestration] Toggle registration failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Silences the Primary and Translate NHotkey slots so they don't fire
        /// while PTT hooks are active. A no-op handler replaces the real handler
        /// instead of removing the registration, which avoids exceptions if the
        /// key was never registered in the first place.
        /// </summary>
        private void TearDownTogglePrimaryTranslate()
        {
            try
            {
                // Replace with a no-op so NHotkey's internal table stays consistent
                // but no recording logic fires.
                EventHandler<HotkeyEventArgs> noOp = (_, e) => e.Handled = true;
                HotkeyManager.Current.AddOrReplace(
                    "Primary",   WpfKey.F8, ModifierKeys.None, noOp);
                HotkeyManager.Current.AddOrReplace(
                    "Translate", WpfKey.F9, ModifierKeys.None, noOp);
            }
            catch
            {
                // Swallow — keys may not be registered yet on first call.
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Private: PTT mode (WH_KEYBOARD_LL)
        // ══════════════════════════════════════════════════════════════════

        private void RegisterPtt(WpfKey primary, WpfKey translate)
        {
            try
            {
                Keys wfPrimary   = WpfKeyToWinForms(primary);
                Keys wfTranslate = WpfKeyToWinForms(translate);

                // Primary PTT hook — fires OnPttPrimaryKeyDown / Up
                _pttPrimaryHook = new LowLevelKeyboardHook(wfPrimary);
                _pttPrimaryHook.KeyPressed  += _onPttPrimaryStart;
                _pttPrimaryHook.KeyReleased += _onPttPrimaryStop;

                // Translate PTT hook — fires OnPttTranslateKeyDown / Up.
                // Separate hook instance so each key carries its own mode identity.
                _pttTranslateHook = new LowLevelKeyboardHook(wfTranslate);
                _pttTranslateHook.KeyPressed  += _onPttTranslateStart;
                _pttTranslateHook.KeyReleased += _onPttTranslateStop;

                _pttActive = true;
                System.Diagnostics.Debug.WriteLine(
                    $"[HotkeyOrchestration] PTT mode registered: Primary={wfPrimary}, Translate={wfTranslate}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[HotkeyOrchestration] PTT hook registration failed: {ex.Message}");
                TearDownPtt();
            }
        }

        private void TearDownPtt()
        {
            if (_pttPrimaryHook != null)
            {
                _pttPrimaryHook.KeyPressed  -= _onPttPrimaryStart;
                _pttPrimaryHook.KeyReleased -= _onPttPrimaryStop;
                _pttPrimaryHook.Dispose();
                _pttPrimaryHook = null;
            }

            if (_pttTranslateHook != null)
            {
                _pttTranslateHook.KeyPressed  -= _onPttTranslateStart;
                _pttTranslateHook.KeyReleased -= _onPttTranslateStop;
                _pttTranslateHook.Dispose();
                _pttTranslateHook = null;
            }

            _pttActive = false;
        }

        // ══════════════════════════════════════════════════════════════════
        // Key enum conversion helpers
        // ══════════════════════════════════════════════════════════════════

        private static bool TryParseWpfKey(string keyName, out WpfKey result)
        {
            try
            {
                result = (WpfKey)Enum.Parse(typeof(WpfKey), keyName, ignoreCase: true);
                return true;
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[HotkeyOrchestration] Cannot parse WPF key: '{keyName}'");
                result = WpfKey.None;
                return false;
            }
        }

        /// <summary>
        /// Maps a WPF Key enum value to a WinForms Keys enum value.
        /// Both are backed by Windows Virtual-Key codes, so the integer values
        /// are identical for all function keys (F1–F24) and alphanumeric keys.
        /// This explicit conversion avoids any ambiguity and documents intent.
        /// </summary>
        private static Keys WpfKeyToWinForms(WpfKey key)
        {
            // Fast path: VK codes are identical for F-keys and standard keys.
            // KeyInterop.VirtualKeyFromKey gives us the Win32 VK code directly.
            int vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
            return (Keys)vk;
        }

        // ══════════════════════════════════════════════════════════════════
        // IDisposable
        // ══════════════════════════════════════════════════════════════════

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            TearDownPtt();
            // NHotkey hooks are cleaned up by the library when the WPF app exits.
        }
    }
}
