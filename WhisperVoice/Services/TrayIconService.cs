using System;
using System.IO;

namespace WhisperVoice.Services
{
    public interface ITrayIconService : IDisposable
    {
        void Initialize(string iconPath, string appName, string restoreMenuText, string notepadMenuText, string exitMenuText);
        void SetRecordingState(bool isRecording);
        event EventHandler OnRestoreRequested;
        event EventHandler OnNotepadRequested;
        event EventHandler OnExitRequested;
    }

    public class TrayIconService : ITrayIconService
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private extern static bool DestroyIcon(IntPtr handle);

        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private System.Drawing.Icon? _originalIcon;
        private IntPtr _currentIconHandle = IntPtr.Zero;

        public event EventHandler? OnRestoreRequested;
        public event EventHandler? OnNotepadRequested;
        public event EventHandler? OnExitRequested;

        public void Initialize(string iconPath, string appName, string restoreMenuText, string notepadMenuText, string exitMenuText)
        {
            _originalIcon = new System.Drawing.Icon(iconPath);
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = _originalIcon,
                Visible = true,
                Text = appName
            };

            _trayIcon.DoubleClick += (_, _) => OnRestoreRequested?.Invoke(this, EventArgs.Empty);

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add(restoreMenuText, null, (_, _) => OnRestoreRequested?.Invoke(this, EventArgs.Empty));
            menu.Items.Add(notepadMenuText, null, (_, _) => OnNotepadRequested?.Invoke(this, EventArgs.Empty));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(exitMenuText, null, (_, _) => OnExitRequested?.Invoke(this, EventArgs.Empty));
            
            _trayIcon.ContextMenuStrip = menu;
        }

        public void SetRecordingState(bool isRecording)
        {
            if (_trayIcon == null || _originalIcon == null) return;

            if (_currentIconHandle != IntPtr.Zero)
            {
                DestroyIcon(_currentIconHandle);
                _currentIconHandle = IntPtr.Zero;
            }

            if (isRecording)
            {
                using var bmp = _originalIcon.ToBitmap();
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.FillEllipse(System.Drawing.Brushes.Red, bmp.Width - 14, bmp.Height - 14, 14, 14);
                    g.DrawEllipse(new System.Drawing.Pen(System.Drawing.Color.White, 2), bmp.Width - 14, bmp.Height - 14, 14, 14);
                }
                _currentIconHandle = bmp.GetHicon();
                _trayIcon.Icon = System.Drawing.Icon.FromHandle(_currentIconHandle);
            }
            else
            {
                _trayIcon.Icon = _originalIcon;
            }
        }

        public void Dispose()
        {
            if (_currentIconHandle != IntPtr.Zero)
            {
                DestroyIcon(_currentIconHandle);
                _currentIconHandle = IntPtr.Zero;
            }

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }
    }
}
