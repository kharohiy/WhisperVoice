# WhisperVoice — Architecture & Engineering Wiki

> **Definitive technical manifest** for contributors and maintainers.  
> Covers privacy guarantees, runtime topology, threading boundaries, and the Engineering Git Workflow that governs every commit to this repository.

---

## 1. Privacy & Transient Data Lifecycle

WhisperVoice is built on an absolute **Privacy-First** foundation. No voice data, transcription text, or user prompts are transmitted to external servers or permanently stored on disk beyond the duration of a single inference cycle.

### 1.1 Transient Audio Capture (`temp.wav`)

Voice data is captured exclusively to a **local, transient file** at a deterministic path:

```
%TEMP%\WhisperVoice_temp.wav
```

This file is the **sole audio artifact** created during operation. It exists only while the inference pipeline is active. It is never copied, renamed, or archived by the application.

### 1.2 File Purge Lifecycle

The transient audio file is purged at **two enforcement points**:

| Phase | Trigger | Action |
|---|---|---|
| **Startup** | `MainWindow()` constructor | `CleanupTempFiles()` removes any orphaned `temp.wav` and orphaned `.part` model downloads from a prior crash |
| **Shutdown** | `Application.Current.Exit` event | `CleanupTempFiles()` is called again as the final clean-up gate before process exit |

This dual-purge guarantees that even an abrupt kill signal on first launch cannot leave audio data behind from a prior abnormal exit.

### 1.3 Log-Hardening: `[REDACTED_PROMPT]` Masking

`whisper-cli` is invoked with a `--prompt` argument carrying user dictionary content. To ensure this content never lands in `whisper_debug.log`, `WhisperExecutionService` applies an explicit masking pass over the argument list before logging:

```csharp
// WhisperExecutionService.RunAsync — log sanitizer loop
if (arg == "--prompt")
{
    logBuilder.Append(" --prompt");
    maskNext = true;   // ← next token is the prompt value
    continue;
}
if (maskNext)
{
    logBuilder.Append(" [REDACTED_PROMPT]");  // ← value replaced
    maskNext = false;
    continue;
}
```

**Guarantee:** Transcription output text is never written to the diagnostic log. The log pipeline is restricted to execution state transitions, hardware detection events, and process exit codes.

---

## 2. Runtime Directory Topography

The application uses two distinct root directories: the **binary directory** (static, read-only at runtime) and the **AppData directory** (user-writable, persistent).

### 2.1 Paths at a Glance

| Artifact | Debug Profile | Release / Production |
|---|---|---|
| Executable | `WhisperVoice\bin\Debug\net8.0-windows\WhisperVoice.exe` | `<InstallDir>\WhisperVoice.exe` |
| `settings.json` | `%AppData%\WhisperVoice\settings.json` | `%AppData%\WhisperVoice\settings.json` |
| AI Models | `<ExeDir>\models\*.bin` | `<InstallDir>\models\*.bin` |
| Hallucination filter | `%AppData%\WhisperVoice\dictionary\hallucinations.json` | `%AppData%\WhisperVoice\dictionary\hallucinations.json` |
| Diagnostic log | `%AppData%\WhisperVoice\whisper_debug.log` | `%AppData%\WhisperVoice\whisper_debug.log` |
| Transient audio | `%TEMP%\WhisperVoice_temp.wav` | `%TEMP%\WhisperVoice_temp.wav` |

### 2.2 AppData Root (`AppSettings.AppDataDir`)

All user-specific, persistent state lives under a single root resolved at runtime:

```csharp
public static string AppDataDir =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WhisperVoice");
```

This single source of truth means no path is ever hardcoded in service code. New services that need persistent storage must derive their paths from `AppSettings.AppDataDir`.

### 2.3 Model Storage & SHA-256 Integrity

Downloaded `.bin` model files are stored in `<ExeDir>\models\`. Every file is validated immediately after download using `IncrementalHash` (streaming SHA-256) to prevent loading corrupted or tampered model data into VRAM:

```csharp
// ModelDownloadService — streaming integrity check
using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
// ... buffer loop ...
var computed = Convert.ToHexString(hash.GetHashAndReset()).ToLower();
if (computed != expectedSha256)
    throw new CryptographicException("SHA-256 mismatch — file deleted.");
```

---

## 3. Threading & Interop Architecture

### 3.1 NAudio WASAPI — MTA Background Threads

`WasapiCapture` and `WasapiLoopbackCapture` internally create COM objects on **MTA (Multi-Threaded Apartment) background threads**. This is intentional and correct for WASAPI, but it carries strict interaction rules:

- **No UI calls** may be made directly from NAudio event callbacks (`DataAvailable`, `RecordingStopped`).
- Peak meter values and silence detection signals are **marshaled asynchronously** back to the WPF Dispatcher thread before touching any UI element:

```csharp
service.PeakAvailable += val =>
    Dispatcher.InvokeAsync(() => VuMeter.Value = val);   // ← safe cross-thread dispatch

service.SilenceDetected += () =>
    Dispatcher.InvokeAsync(() => OnVadSilenceDetected()); // ← always on UI thread
```

- `AudioCaptureService` owns all NAudio objects and is the **only** class permitted to call `StartRecording()` / `StopRecording()` on them. Higher layers interact exclusively through `IAudioCaptureService`.

### 3.2 STA Marshaling — `ClipboardService`

`System.Windows.Clipboard.SetText()` is a COM API that **requires STA (Single-Threaded Apartment) thread state**. Calling it from a background thread raises `ThreadStateException`.

`ClipboardService` solves this by explicitly marshaling back to the WPF application's STA UI thread via the Application Dispatcher:

```csharp
// ClipboardService.CopyAndPasteAsync
Application.Current.Dispatcher.Invoke(() =>
{
    Clipboard.SetText(text);          // ← runs on STA thread
});
// InputSimulator.Ctrl+V fires immediately after, also on UI thread
```

This is the **canonical pattern** for any new service that must interact with Win32/COM APIs requiring STA affinity.

### 3.3 Atomic Race-Condition Guards

`RecordingOrchestrationService` protects its state machine with two independent hardware-level atomic locks using `Interlocked.Exchange`:

| Guard | Protects | Behavior |
|---|---|---|
| `_startGuard` | `StartRecording()` | If a start is already in progress, all concurrent duplicate calls are **silently discarded** |
| `_stopGuard` | `StopAndProcessAsync()` | Guarantees exactly **one** inference pipeline task per recording, regardless of overlapping hotkey events |

```csharp
// Pattern used by both guards
if (Interlocked.Exchange(ref _startGuard, 1) != 0) return; // ← atomic early exit
try   { /* critical section */ }
finally { Interlocked.Exchange(ref _startGuard, 0); }       // ← always released
```

The `Parallel.For` regression test in `RecordingOrchestratorTests.RapidStartTriggers_EarlyLock_DiscardsDuplicate` validates this guarantee on every CI run.

---

## 4. Engineering Git Workflow — The Commit Gate

### 4.1 Core Rule: No Mutation Without Test Coverage

> **Every logical change to a service contract, interface, or state machine must be accompanied by a corresponding regression test update.**

This rule applies to:
- Extracting or adding methods to `RecordingOrchestrationService`, `ModelConfigService`, or any class in `WhisperVoice\Services\`.
- Modifying constructor signatures (DI contract changes).
- Changing filtering logic in `HallucinationFilter` or `TextPostProcessorService`.
- Altering the domain whitelist in `ModelConfigService`.

### 4.2 Pre-Commit Hook — Automated Test Gate

A Git pre-commit hook enforces that `dotnet test` passes before any commit is accepted locally. Install it once after cloning:

**Step 1 — Create the hook file:**

```bash
# Run from the repository root (Git Bash / WSL)
cp .git/hooks/pre-commit.sample .git/hooks/pre-commit
```

**Step 2 — Replace its contents with:**

```bash
#!/usr/bin/env bash
# WhisperVoice Pre-Commit Hook — Commit Gate
# Blocks commits if any unit test fails.

echo "🔒 [Pre-Commit Gate] Running dotnet test..."

dotnet test WhisperVoice.Tests/WhisperVoice.Tests.csproj \
    --no-build \
    --verbosity minimal

EXIT_CODE=$?

if [ $EXIT_CODE -ne 0 ]; then
    echo ""
    echo "❌ COMMIT BLOCKED — One or more tests failed."
    echo "   Fix the failing tests before committing."
    exit 1
fi

echo "✅ All tests passed. Proceeding with commit."
exit 0
```

**Windows (PowerShell alternative — `pre-commit.ps1`):**

```powershell
# .git/hooks/pre-commit (PowerShell fallback for Windows without bash)
$result = dotnet test WhisperVoice.Tests\WhisperVoice.Tests.csproj --no-build --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ COMMIT BLOCKED — Tests failed." -ForegroundColor Red
    exit 1
}
Write-Host "✅ Tests passed." -ForegroundColor Green
exit 0
```

> [!IMPORTANT]
> The hook uses `--no-build` to keep it fast. Always ensure the solution compiles (`dotnet build`) before committing. The hook runs in **under 5 seconds** on the current 17-test suite.

### 4.3 Commit Message Convention

Use the following prefix convention for clarity in `git log`:

| Prefix | Use case |
|---|---|
| `feat:` | New user-facing feature |
| `refactor:` | Internal restructuring with no behavior change |
| `test:` | Adding or updating test cases only |
| `fix:` | Bug fix |
| `docs:` | Documentation-only changes (like this file) |
| `security:` | Hardening changes (SHA-256, domain whitelist, guards) |

---

## 5. Test Suite Reference

Current test coverage (`WhisperVoice.Tests`, 17 tests, all green):

| Test Class | Coverage Area | Tests |
|---|---|---|
| `TextPostProcessorTests` | Whisper timestamp/tag stripping regex | 5 |
| `HallucinationFilterTests` | Dictionary-based false-positive filtering | 3 |
| `ModelConfigServiceTests` | Network isolation, domain whitelist, 404 fallback | 3 |
| `RecordingOrchestratorTests` | Toggle/PTT state machine, Early Lock race guard | 3 |

Run the full suite:

```bash
dotnet test WhisperVoice.Tests/WhisperVoice.Tests.csproj
```

Expected output:
```
Пройден! : не пройдено  0, пройдено  17, пропущено  0, всего  17
```
