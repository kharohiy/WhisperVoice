---
name: whisper-build
description: Rules and commands for building, compiling, and verifying the WhisperVoice WPF app and native libraries.
---

# WhisperVoice Build and Verification Rules

This skill provides the mandatory guidelines for building and testing the application to ensure release readiness.

## Verification Rules
1. **Always run tests after backend changes**: See `resources/build_spec.json` for specific coverage targets and commands.
2. **Native Binaries**: The `whisper.dll` and `ggml*.dll` libraries must be properly copied to the output directory upon build.
3. **No UI Blocking**: Hotkeys and async calls must not block the main WPF thread.
4. **Dependency Check**: Do not update .NET runtime versions without explicit user request.

## Available Resources
- `resources/build_spec.json` contains detailed rules, test commands, and coverage targets.
