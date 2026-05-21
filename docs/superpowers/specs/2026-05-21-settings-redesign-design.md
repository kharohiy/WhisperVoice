# SettingsWindow Redesign Spec

## Purpose
Redesign `SettingsWindow.xaml` to match the modern aesthetic of `MainWindow.xaml`, utilizing a two-column layout with vertical tabs on the left and white content cards on the right.

## Constraints
- Must maintain all existing functionality.
- Must continue to support dynamic localization (`DynamicResource`) for all text elements, including the new tab headers.

## Architecture & Layout
- **Window Size**: Increased width (~600px) and appropriate height (~550px) to comfortably fit a 2-column layout.
- **Left Navigation**:
  - Width: ~180px.
  - Implementation: A `ListBox` styled to look like modern navigation tabs (no default background, blue `#1565C0` selection background with `CornerRadius="6"` and white text when selected).
  - Tabs:
    1. **General (Основные)**: App Language, Model, Startup, System sounds, Clipboard.
    2. **Audio & AI (Аудио и AI)**: VAD settings, Whisper parameters, Push-to-Talk.
    3. **Hotkeys (Горячие клавиши)**: Primary, Translate, Prompt hotkeys.
- **Right Content Area**:
  - Container for the active tab's settings.
  - Will use separate nested Grids bound to the ListBox's `SelectedIndex` (or bound properties) to toggle visibility.
  - Groups of settings will be enclosed in `Border` elements acting as "cards" (White background `#FFFFFF`, rounded corners `CornerRadius="6"`, subtle stroke) over the main `#F0F0F0` window background.
- **Bottom Bar (Footer)**:
  - Spans the width of the window.
  - Contains "Reset Sliders" and "Save & Close" buttons.
  - Buttons will feature `CornerRadius` to match `MainWindow` buttons.

## Localization Changes
Three new string resources are required for the tab headers.
- `SettingsTabGeneral`
- `SettingsTabAudio`
- `SettingsTabHotkeys`

## Verification
- Verify all settings can be saved and loaded correctly.
- Verify hotkey combo boxes still function.
- Verify localization changes instantly apply across the new Tab layout.
