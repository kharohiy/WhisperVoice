using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using WhisperVoice.Services;

namespace WhisperVoice
{
    public class TranscriptionEntry
    {
        public string Text { get; set; } = "";
        public string TimeLabel { get; set; } = "";
        public string Lang { get; set; } = "";
        public bool IsTranslate { get; set; }

        public string ShortText =>
            Text.Length > 120 ? Text[..117] + "..." : Text;

        public string Badge => IsTranslate ? "→EN" : Lang.ToUpper();
    }

    public partial class MainWindow : Window, IMainWindowView
    {
        private readonly MainWindowController _controller;
        
        private readonly NotepadWindow _notepad = new();
        private readonly PromptWindow _promptWindow = new();
        private SettingsWindow _settingsWindow = new();

        private DoubleAnimation? _vadAnim;
        private DateTime _lastDeviceChange = DateTime.MinValue;

        public string BaseDir => AppDomain.CurrentDomain.BaseDirectory;
        public string AppDataDir => AppSettings.AppDataDir;
        public string DictDir => Path.Combine(AppDataDir, "dictionary");
        public string TempWavPath => Path.Combine(Path.GetTempPath(), "WhisperVoice_temp.wav");
        public string TempTxtPath => Path.Combine(Path.GetTempPath(), "WhisperVoice_temp.wav.txt");
        public string LogPath => Path.Combine(AppDataDir, "whisper_debug.log");

        public MainWindow()
        {
            App.ApplyInterfaceLanguage(AppSettings.Load().AppInterfaceLanguage);
            InitializeComponent();

            _controller = new MainWindowController(this);
            _controller.Initialize();

            HistoryList.ItemsSource = _controller.HistoryService.Entries;

            IsVisibleChanged += (_, _) => _controller.OnVisibleChanged();
            System.Windows.Application.Current.Exit += (_, _) => _controller.Dispose();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(HwndHook);
            _controller.RebindHotkeys();
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_DEVICECHANGE = 0x0219;
            if (msg == WM_DEVICECHANGE)
            {
                if ((DateTime.Now - _lastDeviceChange).TotalMilliseconds > 1500)
                {
                    _lastDeviceChange = DateTime.Now;
                    _controller.HandleDeviceChange();
                }
            }
            return IntPtr.Zero;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            HideWindow();
        }

        // ── UI Events ──

        private async void BtnActionRecord_Click(object sender, RoutedEventArgs e) => await _controller.ToggleRecordingAsync();
        private void BtnCancel_Click(object sender, RoutedEventArgs e) => _controller.CancelRecording();

        private void BtnSelectMic_Click(object sender, RoutedEventArgs e)
        {
            var mic = new MicWindow { Owner = this };
            if (mic.ShowDialog() == true)
            {
                _controller.SelectMicrophone(mic.SelectedMicId, mic.SelectedMicName);
            }
        }

        private void BtnLanguageSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow.IsVisible) { _settingsWindow.Activate(); return; }
            _settingsWindow = new SettingsWindow { Owner = this };
            _settingsWindow.Closed += (_, _) => { _controller.UpdateLanguageButton(); _controller.RebindHotkeys(); _controller.UpdateTopBar(); };
            _settingsWindow.Show();
        }

        private void BtnSound_Click(object sender, RoutedEventArgs e)
            => Process.Start(new ProcessStartInfo { FileName = "rundll32.exe", Arguments = "shell32.dll,Control_RunDLL mmsys.cpl,,1", UseShellExecute = true });

        private void BtnPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (_promptWindow.IsVisible) _promptWindow.Hide();
            else { _promptWindow.LoadTags(); _promptWindow.Show(); _promptWindow.Activate(); }
        }

        private void BtnOpenNotepad_Click(object sender, RoutedEventArgs e) => ToggleNotepad();

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e) => _controller.HistoryService.Clear();
        private void BtnExportHistory_Click(object sender, RoutedEventArgs e) => _controller.HistoryService.PromptExport(AppDataDir);

        private void BtnDiagLog_Click(object sender, RoutedEventArgs e)
        {
            string logPath = DiagnosticLogger.Instance.LogPath;
            if (!File.Exists(logPath)) return;
            try { Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = $"\"{logPath}\"", UseShellExecute = false }); }
            catch { }
        }

        private async void HistoryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (HistoryList.SelectedItem is not TranscriptionEntry entry) return;
            try { System.Windows.Clipboard.SetText(entry.Text); ShowCopyFeedback(); } catch { }
            await Task.Delay(200);
            HistoryList.SelectedItem = null;
        }

        private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => _controller.SetVolume((float)(SldVolume.Value / 100.0));

        // ── IMainWindowView Implementation ──

        public void InvokeOnUi(Action action) => Dispatcher.InvokeAsync(action);
        public Task InvokeOnUiAsync(Func<Task> asyncAction) => Dispatcher.InvokeAsync(asyncAction).Task;
        public void DispatcherInvoke(Action action) => Dispatcher.Invoke(action);

        public void ShowErrorPopup(string resourceKey, string? details = null)
        {
            InvokeOnUi(() =>
            {
                string message = TryGetResource(resourceKey, resourceKey);
                if (details != null) message += $"\n\nDetails: {details}";
                string title = TryGetResource("MsgErrorTitle", "Error");
                System.Windows.MessageBox.Show(this, message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            });
        }

        public void UpdateMicLabel(string text, bool ok)
        {
            LblMicName.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            if (!ok)
            {
                LblMicName.Text = text;
                LblMicName.Foreground = System.Windows.Media.Brushes.Red;
            }
            if (LblSelectedDeviceName != null) LblSelectedDeviceName.Text = text;
            VolumePanel.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SetupVolumeSlider(float volume)
        {
            SldVolume.ValueChanged -= SldVolume_ValueChanged;
            SldVolume.Value = volume * 100;
            SldVolume.ValueChanged += SldVolume_ValueChanged;
            VolumePanel.Visibility = Visibility.Visible;
        }

        public void SyncVolumeFromSystem(float volume)
        {
            SldVolume.ValueChanged -= SldVolume_ValueChanged;
            SldVolume.Value = volume * 100;
            SldVolume.ValueChanged += SldVolume_ValueChanged;
        }

        public void UpdateLanguageButton(string langName, string? activeKey = null)
        {
            if (LblCurrentLanguage != null)
                LblCurrentLanguage.Text = string.IsNullOrEmpty(activeKey) ? $"🌐 {langName}" : $"🌐 {langName} [{activeKey}]";
        }

        public void UpdateTopBar(bool isPtt, string defaultText, string privacyTooltip)
        {
            if (LblHotkeyMode != null) LblHotkeyMode.Text = defaultText;
            if (IconPrivacy != null) IconPrivacy.ToolTip = privacyTooltip;
        }

        public void ShowProcessingPanel(bool show)
        {
            ProcessingPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) LblStatus.Text = "";
        }

        public void SetStatusText(string text) => LblStatus.Text = text;

        public void SetMicNameText(string text, bool isErrorOrProcessing)
        {
            LblMicName.Text = text;
            LblMicName.Foreground = isErrorOrProcessing ? System.Windows.Media.Brushes.Orange : System.Windows.Media.Brushes.Red;
            LblMicName.Visibility = Visibility.Visible;
        }

        public void StartVadAnimation()
        {
            _vadAnim ??= new DoubleAnimation { From = 1.0, To = 0.1, Duration = TimeSpan.FromSeconds(0.75), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            VadDot.BeginAnimation(UIElement.OpacityProperty, _vadAnim);
            VadPanel.Visibility = Visibility.Visible;
        }

        public void StopVadAnimation()
        {
            VadDot.BeginAnimation(UIElement.OpacityProperty, null);
            VadDot.Opacity = 0;
            VadPanel.Visibility = Visibility.Collapsed;
        }

        public void SetVuMeterValue(double value) => VuMeter.Value = value;

        public void SetHeroButtonState(string resourceKey, bool isEnabled, string backgroundColorHex, string foregroundColorHex)
        {
            BtnActionRecord.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, resourceKey);
            BtnActionRecord.IsEnabled = isEnabled;
            BtnActionRecord.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom(backgroundColorHex)!;
            BtnActionRecord.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom(foregroundColorHex)!;
        }

        public void SetRecordingState(bool isRecording)
        {
            VolumePanel.Visibility = Visibility.Visible;
            ShowProcessingPanel(false);
        }

        public void SetProcessingState()
        {
            VolumePanel.Visibility = Visibility.Collapsed;
            ShowProcessingPanel(true);
        }

        public void SetIdleState()
        {
            ShowProcessingPanel(false);
        }

        public void ShowMissingModelWindow() => new MissingModelWindow { Owner = this }.ShowDialog();

        public void ToggleNotepad()
        {
            if (_notepad.IsVisible) _notepad.Hide(); else { _notepad.Show(); _notepad.Activate(); }
        }

        public async void ShowCopyFeedback()
        {
            CopyFeedback.Visibility = Visibility.Visible;
            await Task.Delay(1200);
            CopyFeedback.Visibility = Visibility.Collapsed;
        }

        public void ShowAndActivate() { Show(); Activate(); }
        public void HideWindow() => Hide();
        public bool IsWindowVisible => IsVisible;
        public void UpdateTimer(string text) => LblMicName.Text = text;

        public string TryGetResource(string key, string fallback)
        {
            try { return (string)FindResource(key); } catch { return fallback; }
        }
    }
}
