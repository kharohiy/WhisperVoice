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
            string modelsDir = AppSettings.ModelsDir;

            var win = new ModelsWindow(modelsDir, onModelAdded: () => 
            {
                var files = Directory.GetFiles(modelsDir, "*.bin");
                if (files.Length > 0 && !File.Exists(AppSettings.Load().LastModelPath))
                {
                    var settings = AppSettings.Load();
                    settings.LastModelPath = files[0];
                    settings.Save();
                }
            }) { Owner = this };
            win.ShowDialog();

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
