using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;

namespace WhisperVoice
{
    public partial class PromptWindow : Window
    {
        private string dictPath = Path.Combine(
            AppSettings.AppDataDir, "dictionary", "dictionary.txt");
        public ObservableCollection<string> Tags { get; set; } = new ObservableCollection<string>();

        public PromptWindow()
        {
            InitializeComponent();
            LbTags.ItemsSource = Tags;
            LoadTags();
            LoadTranslatePrompt();
        }

        public void LoadTags()
        {
            Tags.Clear();
            if (File.Exists(dictPath))
            {
                var text = File.ReadAllText(dictPath);
                var words = text.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(w => w.Trim())
                                .Where(w => w.Length > 0);
                foreach (var w in words) Tags.Add(w);
            }
        }

        private void LoadTranslatePrompt()
        {
            TxtTranslatePrompt.Text = AppSettings.Load().PromptTranslate;
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            var text = TxtInput.Text.Trim().Replace(",", "");
            if (!string.IsNullOrEmpty(text) && !Tags.Contains(text))
            {
                Tags.Add(text);
                TxtInput.Clear();
            }
            else if (Tags.Contains(text))
            {
                System.Windows.MessageBox.Show("Такой тег уже есть!", "Внимание");
            }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (LbTags.SelectedItem != null)
            {
                Tags.Remove(LbTags.SelectedItem.ToString() ?? "");
                TxtInput.Clear();
            }
        }

        private void LbTags_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (LbTags.SelectedItem != null) TxtInput.Text = LbTags.SelectedItem.ToString() ?? "";
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Save primary tag list
                var dir = Path.GetDirectoryName(dictPath);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(dictPath, string.Join(", ", Tags));

                // Save translate prompt to AppSettings
                var settings = AppSettings.Load();
                settings.PromptTranslate = TxtTranslatePrompt.Text.Trim();
                settings.Save();

                this.Hide();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Ошибка сохранения: {ex.Message}", "Ошибка");
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
        }
    }
}