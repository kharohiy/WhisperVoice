using System.Collections.Generic;
using System.Windows;

namespace WhisperVoice
{
    public partial class SettingsWindow : Window
    {
        private AppSettings _settings;

        // ── Словарик языков (Отображаемое имя -> код для Whisper) ──
        private static readonly Dictionary<string, string> LanguageMap = new()
        {
            { "English",      "en" },
            { "Русский",      "ru" },
            { "Українська",   "uk" },
            { "Polski",       "pl" },
            { "Deutsch",      "de" },
            { "Español",      "es" },
            { "Français",     "fr" }
        };

        public SettingsWindow()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
            LoadSettings();
        }

        private void LoadSettings()
        {
            // Наполняем ComboBox прямо из словаря (чтобы не было конфликта с XAML)
            LanguageCombo.ItemsSource = LanguageMap.Keys;

            string displayName = "Русский";

            foreach (var kvp in LanguageMap)
            {
                if (kvp.Value == _settings.LanguageF8)
                {
                    displayName = kvp.Key;
                    break;
                }
            }

            // Устанавливаем текущий язык
            LanguageCombo.SelectedItem = displayName;
        }

        private void BtnSaveClose_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            this.Close();
        }

        private void SaveSettings()
        {
            // Берем выбранную строку и находим ее код (например, "Français" -> "fr")
            if (LanguageCombo.SelectedItem is string displayName && LanguageMap.TryGetValue(displayName, out string code))
            {
                _settings.LanguageF8 = code;
                _settings.Save();
            }
        }
    }
}