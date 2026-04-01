# Building the WhisperVoice Installer with Inno Setup

## Prerequisites

| Requirement | Notes |
|---|---|
| **Inno Setup 6.3+** | https://jrsoftware.org/isdl.php — free, download the full installer |
| **WhisperVoice built in Release** | Must be compiled before running the `.iss` script |

---

## Step 1 — Build the application

In the project root, run:

```powershell
dotnet publish -c Release
```

This produces the output in:
```
bin\Release\net8.0-windows\
```

> If you use `--self-contained`, the output lands in `bin\Release\net8.0-windows\win-x64\publish\`.
> In that case, update the `Source:` line in `installer.iss` to match.

---

## Step 2 — Open the script in Inno Setup Compiler

1. Launch **Inno Setup Compiler** (search Start Menu, or navigate to `C:\Program Files (x86)\Inno Setup 6\Compil32.exe`).
2. Go to **File → Open** and select `installer.iss` from the WhisperVoice project root.

---

## Step 3 — Verify the `[Files]` source path

Open `installer.iss` and confirm this line matches your actual build output:

```iss
Source: "bin\Release\net8.0-windows\*"; ...
```

The path is relative to the `.iss` file location (the project root).

---

## Step 4 — Compile

Press **F9** (or **Build → Compile**).

Inno Setup will:
- Bundle all files from the Release folder
- Automatically **exclude** `*.pdb` and `*.bin` files (AI models stay out)
- Produce `Output\WhisperVoice_Setup_v1.0.exe`

If there are errors, check the **Messages** pane at the bottom of the Compiler window.

---

## Step 5 — Test the installer

Run `Output\WhisperVoice_Setup_v1.0.exe` on a clean machine (or VM) to verify:

- App installs to `C:\Program Files\WhisperVoice\` (or the user-chosen path).
- Settings, logs, and dictionary write to `%LocalAppData%\WhisperVoice\` — **not** the install folder.
- On first run with no model, the **MissingModelWindow** appears and the download link works.
- The `.NET 8` warning fires on a machine without the runtime.

---

## What is intentionally excluded from the installer

| Excluded | Why |
|---|---|
| `*.bin` (AI models) | Up to 3 GB — too large to bundle. Users download via the in-app prompt. |
| `*.pdb` | Debug symbols. Not needed by end users. |
| `*.xml` | Intellisense XML docs from NuGet packages. |

---

## Distributing the model separately

When users launch WhisperVoice without a model, the **MissingModelWindow** guides them to:

```
https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin?download=true
```

After downloading, they point the app to the file via **Settings → Whisper Model**.

---

## Optional: Signing the installer

To avoid Windows SmartScreen warnings, sign the output `.exe` with a code-signing certificate:

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `
  /f MyCert.pfx /p MyPassword `
  Output\WhisperVoice_Setup_v1.0.exe
```

Self-signed certificates will still trigger SmartScreen — only EV certificates suppress it reliably.
