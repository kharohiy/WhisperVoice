using NAudio.CoreAudioApi;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace WhisperVoice
{
    public partial class MicWindow : Window
    {
        public string SelectedMicId   { get; private set; } = "";
        public string SelectedMicName { get; private set; } = "";

        public MicWindow()
        {
            InitializeComponent();
            LoadDevices();
        }

        private void LoadDevices()
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            foreach (var device in devices)
                LbMics.Items.Add(new { Id = device.ID, Name = device.FriendlyName });
        }

        private void SelectAndClose()
        {
            if (LbMics.SelectedItem == null) return;

            dynamic selected = LbMics.SelectedItem;
            SelectedMicId    = selected.Id;
            SelectedMicName  = selected.Name;
            DialogResult     = true;
        }

        private void BtnSelect_Click(object sender, RoutedEventArgs e) => SelectAndClose();
        private void LbMics_MouseDoubleClick(object sender, MouseButtonEventArgs e) => SelectAndClose();

        private void BtnWinSound_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Opens the classic Windows Sound Control Panel directly to the "Recording" tab
                Process.Start(new ProcessStartInfo
                {
                    FileName        = "mmsys.cpl",
                    Arguments       = ",1",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { DiagnosticLogger.Instance.Error("MicWindow", ex, "Failed to open mmsys.cpl"); }
        }
    }
}
