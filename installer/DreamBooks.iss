; Dream Books per-user installer (phase 5d). Built by installer/build.sh, which passes:
;   /DAppVersion=<x.y.z>  /DArch=<x64|arm64>  /DSourceDir=<published app folder>
; No administrator rights, nothing machine-wide, no network. Texts shown to the user are Italian ([CustomMessages]).

#ifndef AppVersion
  #error AppVersion is not defined (use installer/build.sh)
#endif
#ifndef Arch
  #error Arch is not defined (use installer/build.sh)
#endif
#ifndef SourceDir
  #error SourceDir is not defined (use installer/build.sh)
#endif

#define AppName "Dream Books"
#define AppExe "DreamBooks.App.exe"

[Setup]
; Fixed identity across versions: the update and uninstall logic find the previous install through it.
AppId={{8B3F5C1E-4A7D-4E2B-9C61-2D5A7F0E9B34}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=Fausto Fusaro
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installers
OutputBaseFilename=DreamBooks-{#AppVersion}-{#Arch}-setup
SetupIconFile=..\src\DreamBooks.App\Assets\app-icon.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
