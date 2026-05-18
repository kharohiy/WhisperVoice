; ============================================================
;  WhisperVoice — Inno Setup 6 installer script
;  Build the app first:
;    dotnet publish -c Release -r win-x64 --self-contained false
;  Then compile this script with Inno Setup Compiler (F9).
; ============================================================

[Setup]
AppName=WhisperVoice
AppVersion=1.0
AppPublisher=WhisperVoice
AppPublisherURL=https://github.com/your-repo/WhisperVoice
AppSupportURL=https://github.com/your-repo/WhisperVoice/issues

; Install to Program Files without requiring admin — uses user-level AppData for writable data
DefaultDirName={autopf}\WhisperVoice
; Enable directory selection dialog
DisableDirPage=no
; Allow user-level install (no admin required)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Output
OutputDir=Output
OutputBaseFilename=WhisperVoice_Setup_v1.0

; Compression
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; Architecture
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

; Appearance
WizardStyle=modern
DisableProgramGroupPage=yes
DisableWelcomePage=no

[Languages]
Name: "english";   MessagesFile: "compiler:Default.isl"
Name: "russian";   MessagesFile: "compiler:Languages\Russian.isl"
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "german";    MessagesFile: "compiler:Languages\German.isl"
Name: "french";    MessagesFile: "compiler:Languages\French.isl"
Name: "spanish";   MessagesFile: "compiler:Languages\Spanish.isl"
Name: "polish";    MessagesFile: "compiler:Languages\Polish.isl"

[Files]
; ------------------------------------------------------------------
; Main application — Release build output
; Excludes:  *.pdb  (debug symbols, not needed by end-users)
;            *.bin  (AI model files, ~3 GB — user downloads separately)
; ------------------------------------------------------------------
Source: "bin\Release\net8.0-windows\*"; \
  DestDir: "{app}"; \
  Excludes: "*.pdb,*.bin,*.xml"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
; CRITICAL FOR DIAGNOSTIC LOGGER: 
; Grant modify permissions to the app folder so the application 
; can create and write the whisper_diagnostic.log directly in its directory.
Name: "{app}"; Permissions: users-modify
; Models directory with modify permissions
Name: "{app}\models"; Permissions: users-modify

[Icons]
; Start Menu
Name: "{group}\WhisperVoice";             Filename: "{app}\WhisperVoice.exe"
Name: "{group}\Uninstall WhisperVoice";   Filename: "{uninstallexe}"

; Desktop shortcut
Name: "{userdesktop}\WhisperVoice"; Filename: "{app}\WhisperVoice.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Run]
; Offer to launch the app after installation completes
Filename: "{app}\WhisperVoice.exe"; Description: "{cm:LaunchProgram,WhisperVoice}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up any files the app writes to its install dir at runtime
; Includes the new diagnostic log file
Type: filesandordirs; Name: "{app}\*.log"

[Code]
// Optional: warn if .NET 8 Desktop Runtime is not installed
function IsDotNet8Installed(): Boolean;
var
  key: String;
begin
  key := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  Result := RegKeyExists(HKLM, key) or RegKeyExists(HKCU, key);
end;

procedure InitializeWizard();
begin
  if not IsDotNet8Installed() then
    MsgBox(
      '.NET 8 Desktop Runtime was not detected.' + #13#10 +
      'WhisperVoice requires it to run.' + #13#10#13#10 +
      'Please download it from: https://dotnet.microsoft.com/download/dotnet/8.0',
      mbInformation, MB_OK);
end;