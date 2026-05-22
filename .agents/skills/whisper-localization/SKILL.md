---
name: whisper-localization
description: Guidelines for localizing UI strings in WhisperVoice WPF XAML and C# files. Use when adding or modifying UI text.
---

# WhisperVoice Localization Guidelines

WhisperVoice supports multiple languages out-of-the-box (English, Russian, Ukrainian).

## Core Rules

1. **No Hardcoding**: NEVER hardcode UI text directly into `.xaml` or `.cs` files.
2. **Resource Dictionaries**: All new user-facing strings must be defined in the application's localization resource files:
   - `Strings.en.xaml` (English - default)
   - `Strings.ru.xaml` (Russian)
   - `Strings.uk.xaml` (Ukrainian)
3. **XAML Usage**: Use `DynamicResource` to reference strings in XAML.
   ```xml
   <TextBlock Text="{DynamicResource Subtitle_Processing}" />
   ```
4. **C# Usage**: When text needs to be generated in code, fetch it from the Application resources using the correct key.

Always verify that keys match perfectly across all language files to prevent fallback issues or missing string UI elements.
