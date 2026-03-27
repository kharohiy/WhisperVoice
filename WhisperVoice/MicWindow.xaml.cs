using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace WhisperVoice
{
    public partial class MicWindow : Window
    {
        // Теперь ID - это строка, а не int!
        public string SelectedMicId { get; private set; } = "";
        public string SelectedMicName { get; private set; } = "";

        public MicWindow()
        {
            InitializeComponent();
            LoadMicrophones();
        }

        private void LoadMicrophones()
        {
            // Используем современный WASAPI для получения полных имен
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            foreach (var device in devices)
            {
                LbMics.Items.Add(new { Id = device.ID, Name = device.FriendlyName });
            }
        }

        private void SelectAndClose()
        {
            if (LbMics.SelectedItem != null)
            {
                dynamic selected = LbMics.SelectedItem;
                SelectedMicId = selected.Id;
                SelectedMicName = selected.Name;
                DialogResult = true; // Успешное закрытие
            }
        }

        private void BtnSelect_Click(object sender, RoutedEventArgs e) => SelectAndClose();
        private void LbMics_MouseDoubleClick(object sender, MouseButtonEventArgs e) => SelectAndClose();
    }
}
