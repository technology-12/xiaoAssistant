#ifndef AppVersion
  #define AppVersion "1.0.0.0"
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

[Setup]
AppId={{7C166BEC-41F0-48BA-A402-1E18DD35F198}
AppName=晓助手
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Win7WorkAssistant\versions\{#AppVersion}
UsePreviousAppDir=no
PrivilegesRequired=lowest
MinVersion=6.1sp1
ArchitecturesAllowed=x86 x64
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=WorkAssistant-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#SourceDir}\Assets\AppIcon.ico
CloseApplications=yes
RestartApplications=no
AppMutex=Local\Win7WorkAssistant.Desktop
SignTool=release
SignedUninstaller=yes
UninstallDisplayIcon={app}\WorkAssistant.Desktop.exe
SetupLogging=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.tmp,*.bak"

[Icons]
Name: "{userprograms}\晓助手"; Filename: "{app}\WorkAssistant.Desktop.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\晓助手"; Filename: "{app}\WorkAssistant.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建晓助手桌面快捷方式"; Flags: unchecked

[Registry]
Root: HKCU; Subkey: "Software\Win7WorkAssistant"; ValueType: string; ValueName: "VersionDirectory"; ValueData: "{app}"

[Run]
Filename: "{app}\WorkAssistant.Desktop.exe"; Description: "启动晓助手"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
var
  Release: Cardinal;
begin
  Result := (RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040));
  if not Result then
    MsgBox('Microsoft .NET Framework 4.8 is required. Install it using your organization deployment process, then run this installer again.', mbError, MB_OK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  PreviousDirectory, Backup, Executable: String;
  ExitCode: Integer;
begin
  Result := '';
  if RegQueryStringValue(HKCU, 'Software\Win7WorkAssistant', 'VersionDirectory', PreviousDirectory) then
  begin
    Executable := AddBackslash(PreviousDirectory) + 'WorkAssistant.Desktop.exe';
    if FileExists(Executable) then
    begin
      Backup := ExpandConstant('{localappdata}\Win7WorkAssistant\backups\pre-install-{#AppVersion}.sqlite3');
      ForceDirectories(ExtractFileDir(Backup));
      if (not Exec(Executable, '--backup-state "' + Backup + '"', PreviousDirectory, SW_HIDE, ewWaitUntilTerminated, ExitCode)) or (ExitCode <> 0) then
        Result := 'The state database backup failed. Installation was stopped; the previous version remains available.';
    end;
  end;
end;

// Intentionally no recursive removal of versions or user data. Previous versions are rollback candidates.
