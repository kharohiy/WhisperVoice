using System.Windows;

namespace WhisperVoice
{
    public partial class NotepadWindow : Window
    {
        public NotepadWindow()
        {
            InitializeComponent();
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TxtResult.Text))
            {
                // Явно указываем, что используем WPF-версию буфера обмена и окон
                System.Windows.Clipboard.SetText(TxtResult.Text);
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            TxtResult.Clear();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true; // Отменяем полное закрытие
            this.Hide();     // Просто прячем окно
        }
    }
}