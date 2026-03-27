using NAudio.CoreAudioApi;
using NHotkey.Wpf;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WindowsInput;
using WindowsInput.Native;

namespace WhisperVoice
{
    public partial class MainWindow : Window
    {
        private string settingsPath = "settings.ini";
        private string currentMicId = "";
        private string currentMicName = "";

        // Инструменты
        private AudioRecorder recorder = new AudioRecorder();
        private NotepadWindow notepad = new NotepadWindow();
        private HelpWindow helpWindow = new HelpWindow();
        private PromptWindow promptWindow = new PromptWindow();

        private WasapiCapture? _silentCapture;
        private System.Windows.Forms.NotifyIcon trayIcon;
        private MMDevice? currentDevice;
        private InputSimulator inputSim = new InputSimulator();
        private System.Windows.Threading.DispatcherTimer meterTimer = new System.Windows.Threading.DispatcherTimer();

        // Защита от спама и блокировка интерфейса
        private DateTime lastActionTime = DateTime.MinValue;
        private bool isProcessing = false;

        // Пути
        private string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        private string tempWavPath => Path.Combine(baseDir, "temp.wav");
        private string tempTxtPath => Path.Combine(baseDir, "temp.wav.txt");
        private string logPath => Path.Combine(baseDir, "whisper_debug.log");
        private string dictPath => Path.Combine(baseDir, "dictionary", "dictionary.txt");
        private string modelPath => Path.Combine(baseDir, "models", "ggml-large-v3.bin");
        private string whisperExe => Path.Combine(baseDir, "whisper-cli.exe");

        private enum RecordMode { None, Ru, En, Translate }
        private RecordMode activeMode = RecordMode.None;

        public MainWindow()
        {
            InitializeComponent();

            clearLogs();
            CleanupTempFiles();
            SetupTrayIcon();
            LoadSettings();
            SetupHotkeys();

            // Настройка таймера
            meterTimer.Interval = TimeSpan.FromMilliseconds(50);

            // Запуск таймера, если окно уже открыто (фикс IsVisibleChanged)
            if (this.IsVisible) meterTimer.Start();

            this.IsVisibleChanged += (s, e) => {
                if (this.IsVisible)
                {
                    SyncVolumeFromSystem();
                    meterTimer.Start();
                }
                else
                {
                    meterTimer.Stop();                }
            };

            System.Windows.Application.Current.Exit += (s, e) => CleanupTempFiles();
        }

        private void SetupTrayIcon()
        {
            trayIcon = new System.Windows.Forms.NotifyIcon();
            trayIcon.Icon = System.Drawing.SystemIcons.Information;
            trayIcon.Text = "Whisper Voice";
            trayIcon.Visible = true;
            trayIcon.DoubleClick += (s, e) => { this.Show(); this.Activate(); };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("⚙️ Панель управления (F7)", null, (s, e) => { this.Show(); this.Activate(); });
            menu.Items.Add("📝 Окно Блокнота (Ctrl+F7)", null, (s, e) => { if (notepad.IsVisible) notepad.Hide(); else { notepad.Show(); notepad.Activate(); } });
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            var infoTitle = menu.Items.Add("--- Горячие клавиши ---");
            infoTitle.Enabled = false;
            menu.Items.Add("Запись RU/UA:  F8").Enabled = false;
            menu.Items.Add("Запись EN:     F9").Enabled = false;
            menu.Items.Add("Перевод в EN:  Ctrl + F9").Enabled = false;
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("❌ Выход", null, (s, e) =>
            {
                CleanupTempFiles();
                trayIcon.Visible = false;
                trayIcon.Dispose();
                System.Windows.Application.Current.Shutdown();
            });
            trayIcon.ContextMenuStrip = menu;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _silentCapture?.StopRecording();
            _silentCapture?.Dispose();
            e.Cancel = true;
            this.Hide();
        }

        private void SetupHotkeys()
        {
            try
            {
                HotkeyManager.Current.AddOrReplace("ToggleMenu", Key.F7, ModifierKeys.None, OnToggleMenu);
                HotkeyManager.Current.AddOrReplace("RecordRu", Key.F8, ModifierKeys.None, OnRecordRu);
                HotkeyManager.Current.AddOrReplace("RecordEn", Key.F9, ModifierKeys.None, OnRecordEn);
                HotkeyManager.Current.AddOrReplace("Translate", Key.F9, ModifierKeys.Control, OnTranslate);
                HotkeyManager.Current.AddOrReplace("Notepad", Key.F7, ModifierKeys.Control, OnOpenNotepad);
            }
            catch (Exception ex)
            {
                WriteLog($"Ошибка бинда хоткеев: {ex.Message}");
            }
        }

        private bool IsSpam()
        {
            var diff = (DateTime.Now - lastActionTime).TotalMilliseconds;
            lastActionTime = DateTime.Now;
            return diff < 600;
        }

        private void OnToggleMenu(object? sender, NHotkey.HotkeyEventArgs e)
        {
            e.Handled = true;
            if (IsSpam()) return;
            if (this.IsVisible) this.Hide();
            else { this.Show(); this.Activate(); }
        }

        private void OnOpenNotepad(object? sender, NHotkey.HotkeyEventArgs e)
        {
            e.Handled = true;
            if (IsSpam()) return;
            if (notepad.IsVisible) notepad.Hide();
            else { notepad.Show(); notepad.Activate(); }
        }

        private void OnRecordRu(object? sender, NHotkey.HotkeyEventArgs e) { e.Handled = true; if (!IsSpam() && !isProcessing) ToggleRecording(RecordMode.Ru, "ru", "F8", false); }
        private void OnRecordEn(object? sender, NHotkey.HotkeyEventArgs e) { e.Handled = true; if (!IsSpam() && !isProcessing) ToggleRecording(RecordMode.En, "en", "F9", false); }
        private void OnTranslate(object? sender, NHotkey.HotkeyEventArgs e) { e.Handled = true; if (!IsSpam() && !isProcessing) ToggleRecording(RecordMode.Translate, "ru", "Ctrl+F9", true); }

        private async void ToggleRecording(RecordMode mode, string lang, string keyName, bool isTranslate)
        {
            if (string.IsNullOrEmpty(currentMicId))
            {
                this.Show();
                System.Windows.MessageBox.Show("Выберите микрофон!", "Внимание");
                return;
            }

            if (!recorder.IsRecording)
            {
                activeMode = mode;
                if (File.Exists(tempWavPath)) File.Delete(tempWavPath);

                // Остановка тихого захвата на время реальной записи
                _silentCapture?.StopRecording();

                recorder.StartRecording(currentMicId, tempWavPath);
                LblMicName.Text = $"🔴 ИДЕТ ЗАПИСЬ...\n(Нажми {keyName} чтобы остановить)";
                LblMicName.Foreground = System.Windows.Media.Brushes.Red;
            }
            else
            {
                if (activeMode != mode) return;
                recorder.StopRecording();
                activeMode = RecordMode.None;
                isProcessing = true;

                LblMicName.Text = "🧠 ОБРАБОТКА...\n(Подождите)";
                LblMicName.Foreground = System.Windows.Media.Brushes.Orange;

                await Task.Delay(200);
                await Task.Run(() => ProcessWhisper(lang, isTranslate));

                isProcessing = false;
                UpdateMicUI(currentMicName, true);

                // Возвращаем тихий захват для индикатора
                try { _silentCapture?.StartRecording(); } catch { }
            }
        }

        private void ProcessWhisper(string lang, bool isTranslate)
        {
            if (File.Exists(tempTxtPath)) File.Delete(tempTxtPath);
            try
            {
                using (var reader = new NAudio.Wave.WaveFileReader(tempWavPath))
                {
                    if (reader.TotalTime.TotalSeconds < 1.0) return;
                }
            }
            catch { }

            string techPrompt = "";
            if (File.Exists(dictPath))
            {
                techPrompt = File.ReadAllText(dictPath).Replace("\r\n", " ").Replace("\n", " ").Replace("\"", "");
                if (techPrompt.Length > 250) techPrompt = techPrompt.Substring(0, 250);
            }

            string args = $"-m \"{modelPath}\" -f \"{tempWavPath}\" -l {lang} " + (isTranslate ? "-tr " : "") + $"--prompt \"{techPrompt}\" -otxt -nt -np";
            var startInfo = new ProcessStartInfo { FileName = whisperExe, Arguments = args, WorkingDirectory = baseDir, UseShellExecute = false, CreateNoWindow = true };

            try
            {
                using (var process = Process.Start(startInfo)) { process?.WaitForExit(); }
                if (File.Exists(tempTxtPath))
                {
                    string res = File.ReadAllText(tempTxtPath).Trim();
                    string lowerRes = res.ToLower();
                    if (lowerRes.Contains("dimatorzok") || lowerRes.Contains("dima torzok") || lowerRes.Contains("amara.org") ||
                        lowerRes.Contains("thank you for watching") || res.Length <= 2) return;

                    if (!string.IsNullOrEmpty(res))
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(async () =>
                        {
                            System.Windows.Clipboard.SetText(res);
                            await Task.Delay(100);
                            inputSim.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.VK_V);
                        });
                    }
                }
            }
            catch (Exception ex) { WriteLog($"Ошибка Whisper: {ex.Message}"); }
        }

        private void LoadSettings()
        {
            if (File.Exists(settingsPath))
            {
                var lines = File.ReadAllLines(settingsPath);
                if (lines.Length >= 2 && !string.IsNullOrWhiteSpace(lines[0]))
                {
                    currentMicId = lines[0];
                    currentMicName = lines[1];
                    UpdateMicUI(currentMicName, true);
                    SetupVolumeControl();
                    return;
                }
            }
            UpdateMicUI("⚠️ ВЫБЕРИТЕ МИКРОФОН!", false);
        }

        private void SetupVolumeControl()
        {
            try
            {
                if (currentDevice != null)
                {
                    currentDevice.AudioEndpointVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification;
                    _silentCapture?.StopRecording();
                    _silentCapture?.Dispose();
                }

                var enumerator = new MMDeviceEnumerator();
                currentDevice = enumerator.GetDevice(currentMicId);

                _silentCapture = new WasapiCapture(currentDevice);
                _silentCapture.DataAvailable += (s, a) => { };
                _silentCapture.StartRecording();

                currentDevice.AudioEndpointVolume.OnVolumeNotification += AudioEndpointVolume_OnVolumeNotification;
                SldVolume.ValueChanged -= SldVolume_ValueChanged;
                SldVolume.Value = currentDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100;
                SldVolume.ValueChanged += SldVolume_ValueChanged;

                VolumePanel.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { WriteLog($"Ошибка индикатора: {ex.Message}"); }
        }

        private void AudioEndpointVolume_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            this.Dispatcher.Invoke(() => {
                SldVolume.ValueChanged -= SldVolume_ValueChanged;
                SldVolume.Value = data.MasterVolume * 100;
                SldVolume.ValueChanged += SldVolume_ValueChanged;
            });
        }

        private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (currentDevice != null) currentDevice.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(SldVolume.Value / 100.0);
        }

        private void UpdateMicUI(string text, bool isConfigured)
        {
            LblMicName.Text = text;
            LblMicName.Foreground = isConfigured ? System.Windows.Media.Brushes.Blue : System.Windows.Media.Brushes.Red;
            VolumePanel.Visibility = isConfigured ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnSelectMic_Click(object sender, RoutedEventArgs e)
        {
            MicWindow micWindow = new MicWindow { Owner = this };
            if (micWindow.ShowDialog() == true)
            {
                currentMicId = micWindow.SelectedMicId;
                currentMicName = micWindow.SelectedMicName;
                File.WriteAllLines(settingsPath, new string[] { currentMicId, currentMicName });
                UpdateMicUI(currentMicName, true);
                SetupVolumeControl();
            }
        }

        private void WriteLog(string text) { try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} | {text}\n"); } catch { } }
        private void clearLogs() { if (File.Exists(logPath)) { try { File.Delete(logPath); } catch { } } }
        private void CleanupTempFiles() { try { if (File.Exists(tempWavPath)) File.Delete(tempWavPath); if (File.Exists(tempTxtPath)) File.Delete(tempTxtPath); } catch { } }
        private void BtnSound_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo { FileName = "rundll32.exe", Arguments = "shell32.dll,Control_RunDLL mmsys.cpl,,1", UseShellExecute = true });
        private void BtnPrompt_Click(object sender, RoutedEventArgs e) { if (promptWindow.IsVisible) promptWindow.Hide(); else { promptWindow.LoadTags(); promptWindow.Show(); promptWindow.Activate(); } }
        private void BtnHelp_Click(object sender, RoutedEventArgs e) { if (helpWindow.IsVisible) helpWindow.Hide(); else { helpWindow.Show(); helpWindow.Activate(); } }
        private void BtnOpenNotepad_Click(object sender, RoutedEventArgs e) { if (notepad.IsVisible) notepad.Hide(); else { notepad.Show(); notepad.Activate(); } }
        private void SyncVolumeFromSystem() { if (currentDevice != null) { SldVolume.ValueChanged -= SldVolume_ValueChanged; SldVolume.Value = currentDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100; SldVolume.ValueChanged += SldVolume_ValueChanged; } }
    }
}