; Build with: .\build-installer.ps1 -Platform x64
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{E36A7BAD-5C03-4318-B4E2-BEFE166A2922}
AppName=FtpZilla
AppVersion={#AppVersion}
AppPublisher=judgewest2000
DefaultDirName={localappdata}\Programs\FtpZilla
; Same AppId as the former Winzilla, so this upgrades it; don't reuse its old Programs\Winzilla folder.
UsePreviousAppDir=no
DefaultGroupName=FtpZilla
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\FtpZilla.exe
OutputDir=..\publish
OutputBaseFilename=FtpZilla-Setup-{#Arch}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
SetupIconFile=..\Assets\AppIcon.ico
#if Arch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#elif Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x86compatible
#endif

[Files]
Source: "..\publish\win-{#Arch}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\FtpZilla"; Filename: "{app}\FtpZilla.exe"
Name: "{autodesktop}\FtpZilla"; Filename: "{app}\FtpZilla.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\FtpZilla.exe"; Description: "Launch FtpZilla"; Flags: nowait postinstall skipifsilent
Filename: "{app}\FtpZilla.exe"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[InstallDelete]
; Leftovers from when the app was called Winzilla.
Type: filesandordirs; Name: "{localappdata}\Programs\Winzilla"
Type: files; Name: "{autoprograms}\Winzilla.lnk"
Type: files; Name: "{autodesktop}\Winzilla.lnk"

[Registry]
; Remove the Apps & Features entries written by install.ps1 so only this installer's entry remains.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\FtpZilla"; Flags: deletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\Winzilla"; Flags: deletekey

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;
