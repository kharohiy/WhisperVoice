using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace WhisperVoice
{
    /// <summary>
    /// Shown when LastModelPath is empty or the .bin file no longer exists.
    /// Guides the user to download the model and open Settings.
    /// </summary>
    public partial class MissingModelWindow : Window
    {
        public MissingModelWindow()
        {
            InitializeComponent();
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            // Open Settings modally so the user can configure the model path,
            // then return focus to whatever triggered the check.
            var settingsWindow = new SettingsWindow { Owner = this };
            settingsWindow.ShowDialog();
            Close();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
