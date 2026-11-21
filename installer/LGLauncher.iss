#ifndef LauncherBinary
  #error Supply LauncherBinary when compiling the installer.
#endif

#ifndef ReleaseVersion
  #error Supply ReleaseVersion when compiling the installer.
#endif

#ifndef InstallerAppId
  #define InstallerAppId "LGLauncher.Windows.Stable"
#endif

#ifndef InstallerGroup
  #define InstallerGroup "LG Launcher"
#endif

#ifndef InstallerAppMutex
  #define InstallerAppMutex "Local\LGLauncher.Desktop"
#endif

[Setup]
AppId={#InstallerAppId}
AppName=LG Launcher
AppVersion={#ReleaseVersion}
AppPublisher=LG Launcher
AppPublisherURL=https://github.com/H4RL33/lg_launcher
AppUpdatesURL=https://github.com/H4RL33/lg_launcher/releases/latest
DefaultDirName={localappdata}\Programs\{#InstallerGroup}
DefaultGroupName={#InstallerGroup}
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
AppMutex={#InstallerAppMutex}
CloseApplications=yes
RestartApplications=no
SetupIconFile=..\assets\launcher.ico
UninstallDisplayIcon={app}\LG-Launcher.exe
OutputBaseFilename=LG-Launcher-Setup-{#ReleaseVersion}
VersionInfoVersion={#ReleaseVersion}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
#if ReleaseVersion == "0.1.6"
; Replace the launcher executable when migrating from the old 1.x numbering.
Source: "{#LauncherBinary}"; DestDir: "{app}"; DestName: "LG-Launcher.exe"; Flags: ignoreversion
#else
Source: "{#LauncherBinary}"; DestDir: "{app}"; DestName: "LG-Launcher.exe"
#endif

[INI]
Filename: "{app}\LG-Launcher.install.ini"; Section: "Installation"; Key: "Scope"; String: "User"

[Icons]
Name: "{group}\LG Launcher"; Filename: "{app}\LG-Launcher.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\LG Launcher"; Filename: "{app}\LG-Launcher.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\LG-Launcher.exe"; Description: "Launch LG Launcher"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\LG-Launcher.install.ini"

[Code]
function HasDotNet10(const Root: String): Boolean;
var
  Framework: String;
  Version: TFindRec;
begin
  Result := False;
  if Root = '' then
    Exit;
  Framework := AddBackslash(Root) + 'shared\Microsoft.NETCore.App\';
  if FindFirst(Framework + '10.*', Version) then
  begin
    try
      repeat
        if (Pos('-', Version.Name) = 0) and
           FileExists(Framework + Version.Name + '\Microsoft.NETCore.App.deps.json') and
           FileExists(Framework + Version.Name + '\coreclr.dll') then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(Version);
    finally
      FindClose(Version);
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  Root: String;
  ErrorCode: Integer;
begin
  Result :=
    (RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Root) and HasDotNet10(Root)) or
    HasDotNet10(ExpandConstant('{pf64}\dotnet')) or HasDotNet10(ExpandConstant('{pf64}\dotnet\x64'));
  if not Result then
  begin
    Log('The x64 .NET 10 Runtime is required.');
    if not WizardSilent then
      if MsgBox('LG Launcher requires the x64 .NET 10 Runtime. Open the Microsoft download page?', mbInformation, MB_YESNO) = IDYES then
        ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
var
  StartupCommand: String;
begin
  if (Step = usPostUninstall) and
     RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LG Launcher', StartupCommand) and
     (CompareText(StartupCommand, '"' + ExpandConstant('{app}\LG-Launcher.exe') + '"') = 0) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LG Launcher');
end;
