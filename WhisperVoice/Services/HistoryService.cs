using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace WhisperVoice.Services
{
    public interface IHistoryService
    {
        ObservableCollection<TranscriptionEntry> Entries { get; }
        void AddEntry(string text, string lang, bool isTranslate);
        void Clear();
        void PromptExport(string initialDirectory);
    }

    public class HistoryService : IHistoryService
    {
        private const int MaxHistory = 10;
        private readonly HistoryExportService _exportService = new();
        
        public ObservableCollection<TranscriptionEntry> Entries { get; } = new();

        public void AddEntry(string text, string lang, bool isTranslate)
        {
            Entries.Insert(0, new TranscriptionEntry
            {
                Text = text,
                TimeLabel = DateTime.Now.ToString("HH:mm:ss"),
                Lang = lang,
                IsTranslate = isTranslate
            });

            while (Entries.Count > MaxHistory)
            {
                Entries.RemoveAt(Entries.Count - 1);
            }
        }

        public void Clear()
        {
            Entries.Clear();
        }

        public void PromptExport(string initialDirectory)
        {
            if (Entries.Count == 0) return;

            var dialog = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt",
                DefaultExt = "csv",
                FileName = _exportService.GenerateTimestampedFilename("csv"),
                InitialDirectory = initialDirectory
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                try
                {
                    var exportData = Entries.Select(e => (e.TimeLabel, e.Text)).ToList();
                    
                    if (dialog.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        _exportService.ExportToTxt(exportData, dialog.FileName);
                    else
                        _exportService.ExportToCsv(exportData, dialog.FileName);
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Instance.Warn("HistoryService", $"Export failed: {ex.Message}");
                }
            }
        }
    }
}
