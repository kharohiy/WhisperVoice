---
name: whisper-voice-tech
description: Technical specifications for the WhisperVoice WPF application. Use when working on real-time audio capture, memory management, and C++ interop.
---

# Domain Skill: WhisperVoice Technical Specification

## Core Stack
- **UI Framework**: WPF (.NET Core / Modern .NET)
- **Architecture Pattern**: Strict MVVM with decoupled ViewModels, Services, and Repositories.
- **Native Engine**: C++ (`whisper.dll`, `ggml.dll`, Vulkan/CPU variants via `whisper.cpp`).
- **Interoperability**: C# P/Invoke Bindings to unmanaged C++ libraries.

## Low-Level & Memory Constraints
1. **Unmanaged Memory Discipline**:
   - Any wrapper handling `IntPtr` or native COM interfaces (NAudio `MMDevice`, `WasapiCapture`) must implement `IDisposable` with a robust finalizer pattern.
   - Resource cleanup in `Dispose()` or reset methods must always wrap native invocations in individual `try-catch` blocks and guarantee field nullification via `finally` blocks.
2. **Audio Processing Thread Safety**:
   - Real-time audio capture (`AudioCaptureService`, `AudioRecorder`) executes on high-priority multimedia threads.
   - Avoid memory allocations (Garbage Collector pressure) in hot paths, such as audio buffer processing loops or peak amplitude calculations.
   - UI updates from background audio threads must be properly marshaled via the WPF `Dispatcher`.

## Code Generation Requirements
- Always provide precise Unified Diffs or targeted code snippets using modern C# features (pattern matching, file-scoped namespaces, using declarations).
- Prioritize compile-time checks over runtime assumptions.
