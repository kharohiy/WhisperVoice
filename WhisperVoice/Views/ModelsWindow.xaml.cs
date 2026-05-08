using System;
using System.Windows;
using WhisperVoice.Views;

namespace WhisperVoice.Views
{
    public partial class ModelsWindow : Window
    {
        private readonly ModelsManagerControl _ctrl;

        /// <param name="modelsDir">Pass Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models")</param>
        /// <param name="onModelAdded">Called on UI thread after any .bin finishes downloading.
        ///   Wire to settingsWindow.LoadModels() so the ComboBox refreshes automatically.</param>
        public ModelsWindow(string modelsDir, Action? onModelAdded = null)
        {
            InitializeComponent();
            _ctrl = ModelsManagerControl.Create(modelsDir);
            if (onModelAdded is not null)
                _ctrl.ViewModel.ModelFileDownloaded += _ => onModelAdded();
            ControlHost.Content = _ctrl;
            Loaded += async (_, _) => await _ctrl.ViewModel.LoadAsync();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
