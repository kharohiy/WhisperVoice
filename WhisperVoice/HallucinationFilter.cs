using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WhisperVoice.Services
{
    /// <summary>
    /// Loads hallucination patterns from dictionary/hallucinations.json at startup,
    /// creating the file with safe defaults if it does not exist.
    /// Call <see cref="Check"/> instead of the old inline SanityCheck logic.
    /// </summary>
    public class HallucinationFilter
    {
        // ── Defaults (written to disk on first run) ────────────────────────
        private static readonly string[] _defaults = new[]
        {
            "amara.org", "subtitle by", "subtitles by", "subtitled by",
            "transcribed by", "closed captioning", "closed caption",
            "thanks for watching", "thank you for watching",
            "like and subscribe", "please subscribe",
            "dimatorzok", "dima torzok",
            "спасибо за субтитры", "алексею дубровскому", "продолжение следует",
            "редактор субтитров", "субтитры создавал", "субтитры делал",
            "перевод на русский", "субтитры от", "субтитры добавил",
            "спасибо за просмотр", "дима торзок", "дима торжок",
            "[ музыка ]", "[ музыка", "[ music ]", "[music]",
            "[ applause ]", "[applause]", "[ silence ]",
            "♪", "www.", ".com", ".org", ".net"
        };

        private string[] _patterns;

        private static readonly JsonSerializerOptions _jsonOpts =
            new() { WriteIndented = true };

        public HallucinationFilter(string dictionaryDir)
        {
            string path = Path.Combine(dictionaryDir, "hallucinations.json");
            _patterns = Load(path);
        }

        // ── Load / bootstrap ───────────────────────────────────────────────
        private static string[] Load(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    var list = JsonSerializer.Deserialize<string[]>(json, _jsonOpts);
                    if (list is { Length: > 0 }) return list;
                }
                catch (Exception ex) { DiagnosticLogger.Instance.Warn("HallucinationFilter", $"Corrupt dictionary, recreating: {ex.Message}"); }
            }

            // Bootstrap: write defaults to disk so user can edit them
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(_defaults, _jsonOpts));
            }
            catch (Exception ex) { DiagnosticLogger.Instance.Error("HallucinationFilter", ex, "Failed to bootstrap dictionary"); }

            return _defaults;
        }

        /// <summary>
        /// Returns <c>true</c> when the text passes all sanity checks.
        /// <paramref name="cleaned"/> receives the trimmed result on success,
        /// or an empty string on failure.
        /// </summary>
        public bool Check(string text, out string cleaned)
        {
            cleaned = "";
            if (string.IsNullOrWhiteSpace(text)) return false;

            int alphaCount = text.Count(char.IsLetterOrDigit);
            if (alphaCount < 2) return false;

            string lower = text.ToLowerInvariant();
            foreach (string pat in _patterns)
                if (!string.IsNullOrWhiteSpace(pat) && lower.Contains(pat.ToLowerInvariant())) return false;

            cleaned = text.Trim('\0', '\r', '\n', ' ', '\t');
            return cleaned.Length > 0;
        }

        /// <summary>Reload patterns from disk without restarting the app.</summary>
        public void Reload(string dictionaryDir)
        {
            string path = Path.Combine(dictionaryDir, "hallucinations.json");
            _patterns = Load(path);
        }
    }
}
