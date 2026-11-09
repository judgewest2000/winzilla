; Build with: .\build-installer.ps1 -Platform x64
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{E36A7BAD-5C03-4318-B4E2-BEFE166A2922}
AppName=Winzilla
AppVersion={#AppVersion}
AppPublisher=judgewest2000
DefaultDirName={localappdata}\Programs\Winzilla
DefaultGroupName=Winzilla
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\Winzilla.exe
OutputDir=..\publish
OutputBaseFilename=Winzilla-Setup-{#Arch}
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
Name: "{autoprograms}\Winzilla"; Filename: "{app}\Winzilla.exe"
Name: "{autodesktop}\Winzilla"; Filename: "{app}\Winzilla.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\Winzilla.exe"; Description: "Launch Winzilla"; Flags: nowait postinstall skipifsilent
