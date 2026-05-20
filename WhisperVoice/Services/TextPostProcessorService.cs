using System.Text.RegularExpressions;

namespace WhisperVoice.Services
{
    /// <summary>
    /// Cleans whisper output after hallucination filtering, before clipboard paste.
    /// Pipeline: rawResult → HallucinationFilter → TextPostProcessorService → clipboard
    /// </summary>
    public class TextPostProcessorService
    {
                // Matches timestamp lines whisper leaks
        private static readonly Regex _timestampLine =
            new(@"[\[\(]\d{2}:\d{2}:\d{2}\.\d{3}\s*-->\s*\d{2}:\d{2}:\d{2}\.\d{3}[\]\)]", RegexOptions.Compiled);

        // Уничтожает звуковые галлюцинации Whisper: *phone rings*, (coughs), [music]
        private static readonly Regex _acousticTags = 
            new(@"\[.*?\]|\(.*?\)|\*.*?\*|f\d{1,2}\s*key|phone\s*rings?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex _multiSpace =
            new(@" {2,}", RegexOptions.Compiled);

        public string Process(string text)
        {
            // Flatten newlines and tabs to single spaces before further processing
            text = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");

            // 0. Вырезаем все акустические галлюцинации (в скобках и звездочках)
            text = _acousticTags.Replace(text, "").Trim();

            // 1. Strip leaked timestamp segments
            text = _timestampLine.Replace(text, "").Trim();

            // 2. Collapse runs of spaces
            text = _multiSpace.Replace(text, " ").Trim();

            // 3. Auto-capitalize first letter
            if (text.Length > 0 && char.IsLower(text[0]))
                text = char.ToUpper(text[0]) + text[1..];

            return text;
        }
    }
}
