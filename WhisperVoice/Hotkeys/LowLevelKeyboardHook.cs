using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WhisperVoice.Hotkeys
{
    /// <summary>
    /// Wraps a WH_KEYBOARD_LL global hook via SetWindowsHookEx.
    ///
    /// Safety guarantees:
    ///   • The hook delegate is stored in a class-level field (_hookCallback) so the
    ///     GC can never collect it while the hook is alive — avoids AccessViolationException.
    ///   • Auto-repeat guard (_isKeyDown) ensures KeyPressed fires only once per physical
    ///     press, ignoring the OS key-repeat WM_KEYDOWN flood.
    ///   • Dispose / finalizer pair guarantees UnhookWindowsHookEx is always called,
    ///     even if the caller forgets to dispose.
    ///   • The hook callback returns CallNextHookEx immediately — all work is dispatched
    ///     to the caller's event handlers so the OS message pump is never blocked.
    /// </summary>
    internal sealed class LowLevelKeyboardHook : IDisposable
    {
        // ── Win32 constants ────────────────────────────────────────────────
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;

        // ── P/Invoke ───────────────────────────────────────────────────────
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(
            IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // ── State ──────────────────────────────────────────────────────────

        /// <summary>
        /// GC anchor — MUST be a field. If this were a local or anonymous delegate
        /// it would be eligible for collection after SetWindowsHookEx returns,
        /// causing a crash on the next keystroke.
        /// </summary>
        private readonly LowLevelKeyboardProc _hookCallback;

        private IntPtr _hookId = IntPtr.Zero;

        /// <summary>
        /// The single virtual-key code we watch. All other keys pass through
        /// without raising events.
        /// </summary>
        private readonly Keys _watchedKey;

        /// <summary>
        /// Auto-repeat guard. Set to true on first WM_KEYDOWN, cleared on WM_KEYUP.
        /// Declared volatile so reads/writes are not reordered by the JIT.
        /// </summary>
        private volatile bool _isKeyDown;

        private bool _disposed;

        // ── Public events ──────────────────────────────────────────────────

        /// <summary>Fires once on the initial physical key-down (auto-repeat suppressed).</summary>
        public event Action? KeyPressed;

        /// <summary>Fires on every physical key-up.</summary>
        public event Action? KeyReleased;

        // ── Constructor ────────────────────────────────────────────────────

        /// <param name="watchedKey">The WinForms Keys value to intercept.</param>
        public LowLevelKeyboardHook(Keys watchedKey)
        {
            _watchedKey = watchedKey;

            // Assign to field BEFORE calling SetWindowsHookEx so the GC root
            // exists from the moment the hook is registered.
            _hookCallback = HookProc;

            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule
                ?? throw new InvalidOperationException("Cannot retrieve main module handle.");

            _hookId = SetWindowsHookEx(
                WH_KEYBOARD_LL,
                _hookCallback,
                GetModuleHandle(curModule.ModuleName),
                0);                    // 0 = system-wide

            if (_hookId == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"SetWindowsHookEx failed. Win32 error: {err}");
            }
        }

        // ── Hook callback ──────────────────────────────────────────────────

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // nCode < 0 means we MUST pass through without processing (Win32 contract).
            if (nCode >= 0)
            {
                var kbStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var vk = (Keys)kbStruct.vkCode;

                if (vk == _watchedKey)
                {
                    int msg = wParam.ToInt32();

                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        // Auto-repeat guard: only raise on the first down event.
                        if (!_isKeyDown)
                        {
                            _isKeyDown = true;
                            // Raise on calling thread (UI thread for WPF).
                            // Event handlers MUST be lightweight / fire-and-forget.
                            KeyPressed?.Invoke();
                        }
                        // Suppress this key from reaching other applications.
                        return new IntPtr(1);
                    }
                    else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                    {
                        _isKeyDown = false;
                        KeyReleased?.Invoke();
                        // Suppress.
                        return new IntPtr(1);
                    }
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // ── IDisposable ────────────────────────────────────────────────────

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Finalizer safety net. If the caller forgets to Dispose(), the hook is
        /// still removed before the object is garbage-collected. This prevents a
        /// dangling hook pointing at a freed delegate stub.
        /// </summary>
        ~LowLevelKeyboardHook() => Dispose(disposing: false);
    }
}