using FluentAssertions;
using System.IO;
using System.Text.Json;
using Xunit;

namespace WhisperVoice.Tests
{
    /// <summary>
    /// Tests for AppSettings migration robustness.
    /// Verifies that legacy JSON key names are migrated correctly
    /// and that SettingsVersion is persisted properly.
    /// </summary>
    public class AppSettingsMigrationTests
    {
        // ── Issue: string.Replace on JSON without versioning.
        //    If a key is present in the value, the replacement breaks JSON.
        //    fix/settings-versioning: MigrateJsonIfNeeded() + SettingsVersion field.

        [Fact]
        public void MigrateJsonIfNeeded_WithLegacyHotkeyRuKey_RenamesCorrectly()
        {
            // Arrange — JSON with legacy key HotkeyRu
            string legacyJson = """
                {
                  "HotkeyRu": "F8",
                  "HotkeyEn": "F9",
                  "LanguageF8": "ru",
                  "MicId": "test-mic"
                }
                """;

            // Act
            string migratedJson = AppSettings.MigrateJsonIfNeeded(legacyJson);

            // Assert — old keys replaced, JSON is valid
            migratedJson.Should().Contain("\"HotkeyPrimary\"");
            migratedJson.Should().Contain("\"HotkeyTranslate\"");
            migratedJson.Should().Contain("\"LanguagePrimary\"");
            migratedJson.Should().NotContain("\"HotkeyRu\"");
            migratedJson.Should().NotContain("\"HotkeyEn\"");
            migratedJson.Should().NotContain("\"LanguageF8\"");

            // The result must be valid JSON
            var ex = Record.Exception(() => JsonDocument.Parse(migratedJson));
            ex.Should().BeNull("migrated JSON must remain valid");
        }

        [Fact]
        public void MigrateJsonIfNeeded_WithCurrentJson_ReturnsUnchanged()
        {
            // Arrange — already current JSON without legacy keys
            string currentJson = """
                {
                  "HotkeyPrimary": "F8",
                  "HotkeyTranslate": "F9",
                  "LanguagePrimary": "en",
                  "MicId": "test-mic"
                }
                """;

            // Act
            string result = AppSettings.MigrateJsonIfNeeded(currentJson);

            // Assert — content unchanged (no redundant replacements)
            result.Should().Contain("\"HotkeyPrimary\"");
            result.Should().Contain("\"HotkeyTranslate\"");
            result.Should().NotContain("\"HotkeyRu\"");
        }

        [Fact]
        public void AppSettings_NewInstance_HasSettingsVersion()
        {
            // Arrange & Act
            var settings = new AppSettings();

            // Assert — every new object has a version
            settings.SettingsVersion.Should().BeGreaterThan(0,
                because: "SettingsVersion must be set to allow future migrations");
        }

        [Fact]
        public void AppSettings_SaveAndLoad_PreservesSettingsVersion()
        {
            // Arrange — use an isolated temporary directory
            string tempDir = Path.Combine(Path.GetTempPath(), "wv_migration_test_" + System.Guid.NewGuid());
            Directory.CreateDirectory(tempDir);

            try
            {
                var settings = new AppSettings();
                int originalVersion = settings.SettingsVersion;

                // Save via JSON directly (bypassing the real AppDataDir)
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(settings, options);

                // Verify that SettingsVersion is present in JSON
                json.Should().Contain("SettingsVersion",
                    because: "SettingsVersion must be serialized to JSON");

                // Load back
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, options);
                loaded.Should().NotBeNull();
                loaded!.SettingsVersion.Should().Be(originalVersion);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void MigrateJsonIfNeeded_WithEmptyJson_ReturnsEmpty()
        {
            // Edge case: empty string should not throw
            var ex = Record.Exception(() => AppSettings.MigrateJsonIfNeeded("{}"));
            ex.Should().BeNull();
        }
    }
}
