using System.Text.RegularExpressions;

namespace WhisperVoice.Services
{
    /// <summary>
    /// Cleans whisper output after hallucination filtering, before clipboard paste.
    /// Pipeline: rawResult → HallucinationFilter → TextPostProcessorService → clipboard
    /// </summary>
    public class TextPostProcessorService
    {
        // Matches timestamp lines whisper leaks even with -nt/-np flags
        // e.g. [00:00:00.000 --> 00:00:02.500] or (00:00:00.000 --> 00:00:02.500)
        private static readonly Regex _timestampLine =
            new(@"[\[\(]\d{2}:\d{2}:\d{2}\.\d{3}\s*-->\s*\d{2}:\d{2}:\d{2}\.\d{3}[\]\)]",
                RegexOptions.Compiled);

        private static readonly Regex _multiSpace =
            new(@" {2,}", RegexOptions.Compiled);

        public string Process(string text)
        {
            // 1. Strip leaked timestamp segments
            text = _timestampLine.Replace(text, "").Trim();

            // 2. Collapse runs of spaces left after timestamp removal
            text = _multiSpace.Replace(text, " ").Trim();

            // 3. Auto-capitalize first letter
            if (text.Length > 0 && char.IsLower(text[0]))
                text = char.ToUpper(text[0]) + text[1..];

            return text;
        }
    }
}
