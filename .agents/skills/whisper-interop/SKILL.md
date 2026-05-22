---
name: whisper-interop
description: Guidelines for unmanaged C++ interop, P/Invoke, and mandatory automated testing rules in WhisperVoice.
---

# Role & Context
You are an expert Senior Developer specializing in C#/.NET (WPF) and C++ Interop. You are working on "WhisperVoice" — a high-performance voice-to-text desktop utility.

## Architectural Principles
1. **Strict MVVM & SOLID**: 
   - Keep WPF Views completely decoupled from logic. 
   - ViewModels must communicate with Services via dependency injection interfaces.
   - Do not write business logic or state mutations inside UI event handlers.

2. **C++ / C# Interop Rules**:
   - The core recognition engine is implemented in native C++ via `whisper.dll` and `ggml.dll`.
   - All P/Invoke signatures and C# bindings must use explicit marshaling rules (`[DllImport]`, `CallingConvention.Cdecl`).
   - Managed/Unmanaged memory boundaries must be strictly respected. Always implement `IDisposable` in wrappers holding native pointers (`IntPtr`) to prevent memory leaks in real-time audio processing.

3. **Audio & Hardware Aware**:
   - Audio capture services (`AudioCaptureService.cs`, `AudioRecorder.cs`) handle real-time buffers. Code modifications in these files must prioritize thread-safety and avoid allocations inside hot paths to prevent UI stuttering.
   - Respect hardware capabilities (CPU/Vulkan variants of ggml).

## Testing Contract (MANDATORY)

Every modification to a **service interface or implementation** in `WhisperVoice/Services/` **must** be accompanied by a corresponding update to `WhisperVoice.Tests/`.

### Coverage Map

| Production File | Test File | Guard |
|---|---|---|
| `Services/RecordingOrchestrationService.cs` | `RecordingOrchestratorTests.cs` | Toggle/PTT state machine, `_startGuard` race lock |
| `Services/ModelConfigService.cs` | `ModelConfigServiceTests.cs` | Domain whitelist, 404 fallback |
| `HallucinationFilter.cs` | `HallucinationFilterTests.cs` | Dictionary-based phrase filtering |
| `Services/TextPostProcessorService.cs` | `TextPostProcessorTests.cs` | Timestamp/tag regex stripping |

### AI Agent Rule

> Before declaring any task **complete**, the agent must:
> 1. Run `dotnet test WhisperVoice.Tests/WhisperVoice.Tests.csproj` and confirm all tests pass.
> 2. If a service contract (interface method signature, constructor, or enum) was changed, verify that all affected test fixtures compile and remain green.
> 3. If new logic was introduced in a service, add at least one test covering the new code path.

See `ARCHITECTURE_WIKI.md` Section 4 for the full Engineering Git Workflow and the pre-commit hook installation guide.
