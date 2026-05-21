using FluentAssertions;
using System.Linq;
using Xunit;

namespace WhisperVoice.Tests
{
    public class AppSettingsProfileTests
    {
        [Fact]
        public void InitializeDefaultProfiles_PopulatesThreeProfiles_WhenEmpty()
        {
            var settings = new AppSettings();
            settings.CustomProfiles.Should().BeEmpty();

            settings.InitializeDefaultProfiles();

            settings.CustomProfiles.Should().HaveCount(3);
            settings.CustomProfiles.Should().Contain(p => p.Id == "dev" && p.Name == "Developer");
            settings.CustomProfiles.Should().Contain(p => p.Id == "eng" && p.Name == "English Teacher");
            settings.CustomProfiles.Should().Contain(p => p.Id == "copy" && p.Name == "Copywriter");
            
            settings.PromptProfileId.Should().Be("dev");
        }

        [Fact]
        public void InitializeDefaultProfiles_DoesNotOverwrite_IfAlreadyPopulated()
        {
            var settings = new AppSettings();
            settings.CustomProfiles.Add(new WhisperProfile { Id = "custom", Name = "Custom" });

            settings.InitializeDefaultProfiles();

            settings.CustomProfiles.Should().HaveCount(1);
            settings.CustomProfiles.First().Id.Should().Be("custom");
        }
    }
}
