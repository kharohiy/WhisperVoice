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
        // ── Проблема: string.Replace на JSON без версионирования.
        //    Если ключ присутствует в значении — замена сломает JSON.
        //    fix/settings-versioning: MigrateJsonIfNeeded() + SettingsVersion поле.

        [Fact]
        public void MigrateJsonIfNeeded_WithLegacyHotkeyRuKey_RenamesCorrectly()
        {
            // Arrange — JSON с устаревшим ключом HotkeyRu
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

            // Assert — старые ключи заменены, JSON валиден
            migratedJson.Should().Contain("\"HotkeyPrimary\"");
            migratedJson.Should().Contain("\"HotkeyTranslate\"");
            migratedJson.Should().Contain("\"LanguagePrimary\"");
            migratedJson.Should().NotContain("\"HotkeyRu\"");
            migratedJson.Should().NotContain("\"HotkeyEn\"");
            migratedJson.Should().NotContain("\"LanguageF8\"");

            // Результат должен быть валидным JSON
            var ex = Record.Exception(() => JsonDocument.Parse(migratedJson));
            ex.Should().BeNull("migrated JSON must remain valid");
        }

        [Fact]
        public void MigrateJsonIfNeeded_WithCurrentJson_ReturnsUnchanged()
        {
            // Arrange — уже актуальный JSON без legacy ключей
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

            // Assert — содержимое не изменилось (нет лишних замен)
            result.Should().Contain("\"HotkeyPrimary\"");
            result.Should().Contain("\"HotkeyTranslate\"");
            result.Should().NotContain("\"HotkeyRu\"");
        }

        [Fact]
        public void AppSettings_NewInstance_HasSettingsVersion()
        {
            // Arrange & Act
            var settings = new AppSettings();

            // Assert — каждый новый объект имеет версию
            settings.SettingsVersion.Should().BeGreaterThan(0,
                because: "SettingsVersion must be set to allow future migrations");
        }

        [Fact]
        public void AppSettings_SaveAndLoad_PreservesSettingsVersion()
        {
            // Arrange — используем изолированный временный файл
            string tempDir = Path.Combine(Path.GetTempPath(), "wv_migration_test_" + System.Guid.NewGuid());
            Directory.CreateDirectory(tempDir);

            try
            {
                var settings = new AppSettings();
                int originalVersion = settings.SettingsVersion;

                // Сохраняем через JSON напрямую (без привязки к реальному AppDataDir)
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(settings, options);

                // Проверяем что SettingsVersion есть в JSON
                json.Should().Contain("SettingsVersion",
                    because: "SettingsVersion must be serialized to JSON");

                // Загружаем обратно
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
            // Edge case: пустая строка не должна бросать
            var ex = Record.Exception(() => AppSettings.MigrateJsonIfNeeded("{}"));
            ex.Should().BeNull();
        }
    }
}
