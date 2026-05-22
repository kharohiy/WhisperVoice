---
name: whisper-audio-capture
description: Guidelines for real-time audio capture, WASAPI, high-priority threads, and wave buffers in WhisperVoice.
---

# WhisperVoice Audio Capture & Threading Rules

Audio capture is one of the most critical and performance-sensitive layers of WhisperVoice.

## Threading Model
- **WASAPI Capture**: `AudioCaptureService` and `AudioRecorder` use WASAPI (NAudio), which internally relies on Multi-Threaded Apartment (MTA) background threads.
- **UI Dispatch**: Data originating from these audio capture threads (e.g. RMS peak levels, voice activation state) MUST be dispatched to the main UI thread via `Application.Current.Dispatcher` before any UI bindings are updated.

## Performance and Memory Requirements
1. **Zero Allocations in Hot Paths**: The core buffer loop (where wave bytes are processed into float arrays for RMS energy calculation and Whisper inference) executes extremely fast. Do NOT allocate new objects or trigger Garbage Collector (GC) pressure inside this loop.
2. **Unmanaged Resources**: Any class handling raw wave pointers, memory-mapped files, or COM objects MUST implement `IDisposable` securely.
3. **Race Conditions**: Start/Stop actions triggered by hotkeys can fire concurrently. Always use atomic locks (`lock` statements or Interlocked operations) to guard state transitions in `RecordingOrchestrationService`.
