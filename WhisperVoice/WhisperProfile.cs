using System;

namespace WhisperVoice
{
    public class WhisperProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "New Profile";
        public string PromptTags { get; set; } = "";
        public double Temperature { get; set; } = 0.2;
        public bool IsPredefined { get; set; } = false;
    }
}
