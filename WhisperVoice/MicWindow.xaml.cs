using NAudio.CoreAudioApi;
using System.Windows;
using System.Windows.Input;

namespace WhisperVoice
{
    public partial class MicWindow : Window
    {
        public string SelectedMicId { get; private set; } = "";
        public string SelectedMicName { get; private set; } = "";

        public MicWindow()
        {
            InitializeComponent();
            LoadMicrophones();
        }

        private void LoadMicrophones()
        {
            // BUG FIX: MMDeviceEnumerator was never disposed — resource leak.
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            foreach (var device in devices)
                LbMics.Items.Add(new { Id = device.ID, Name = device.FriendlyName });
        }

        private void SelectAndClose()
        {
            if (LbMics.SelectedItem == null) return;

            dynamic selected = LbMics.SelectedItem;
            SelectedMicId = selected.Id;
            SelectedMicName = selected.Name;
            DialogResult = true;
        }

        private void BtnSelect_Click(object sender, RoutedEventArgs e) => SelectAndClose();
        private void LbMics_MouseDoubleClick(object sender, MouseButtonEventArgs e) => SelectAndClose();
    }
}
