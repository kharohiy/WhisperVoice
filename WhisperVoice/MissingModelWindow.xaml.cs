using System.IO;
using System.Diagnostics;
using System.Windows;
using WhisperVoice.Views;

namespace WhisperVoice
{
    /// <summary>
    /// Shown when LastModelPath is empty or the .bin file no longer exists.
    /// </summary>
    public partial class MissingModelWindow : Window
    {
        public MissingModelWindow()
        {
            InitializeComponent();
        }

        private void BtnGetModels_Click(object sender, RoutedEventArgs e)
        {
            string modelsDir = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "models");

            var win = new ModelsWindow(modelsDir, onModelAdded: null) { Owner = this };
            win.ShowDialog();

            // If the user downloaded a model, the Settings ComboBox will refresh
            // via ModelFileDownloaded event when opened next time.
            // Re-check: if a model now exists, close this prompt.
            if (File.Exists(AppSettings.Load().LastModelPath))
                Close();
        }

        private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow { Owner = this };
            settingsWindow.ShowDialog();
            Close();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
