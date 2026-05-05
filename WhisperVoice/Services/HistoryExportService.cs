using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WhisperVoice.Services
{
    /// <summary>
    /// Exports transcription history to .txt or .csv format.
    /// </summary>
    public class HistoryExportService
    {
        public void ExportToCsv(IEnumerable<(string timeLabel, string text)> entries, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("\"Time\",\"Text\"");

            foreach (var (time, text) in entries)
            {
                // Escape quotes in text by doubling them (CSV standard)
                string escapedText = text.Replace("\"", "\"\"");
                sb.AppendLine($"\"{time}\",\"{escapedText}\"");
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        public void ExportToTxt(IEnumerable<(string timeLabel, string text)> entries, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"═══ WhisperVoice Transcription Export ═══");
            sb.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            foreach (var (time, text) in entries)
            {
                sb.AppendLine($"[{time}]");
                sb.AppendLine(text);
                sb.AppendLine();
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// Generate a timestamped filename (e.g., "WhisperVoice_2024-01-15_143025.csv")
        /// </summary>
        public string GenerateTimestampedFilename(string extension)
        {
            return $"WhisperVoice_{DateTime.Now:yyyy-MM-dd_HHmmss}.{extension}";
        }
    }
}
