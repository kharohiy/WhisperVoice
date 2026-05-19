using System;
using System.IO;

namespace WhisperVoice.Services
{
    public interface ITrayIconService : IDisposable
    {
        void Initialize(string iconPath, string appName, string restoreMenuText, string notepadMenuText, string exitMenuText);
        event EventHandler OnRestoreRequested;
        event EventHandler OnNotepadRequested;
        event EventHandler OnExitRequested;
    }

    public class TrayIconService : ITrayIconService
    {
        private System.Windows.Forms.NotifyIcon? _trayIcon;

        public event EventHandler? OnRestoreRequested;
        public event EventHandler? OnNotepadRequested;
        public event EventHandler? OnExitRequested;

        public void Initialize(string iconPath, string appName, string restoreMenuText, string notepadMenuText, string exitMenuText)
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = new System.Drawing.Icon(iconPath),
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

        public void Dispose()
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }
    }
}
