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

InfoBeforeFile=welcome.it.txt
; An update asks to close a running Dream Books, reuses the folder and the choices of the first install.
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[CustomMessages]
italian.TaskIcons=Icone aggiuntive (il collegamento nel menu Start viene creato sempre):
italian.TaskDesktop=Icona sul desktop
italian.TaskPdf=Apri i file PDF con Dream Books
italian.TaskPdfGroup=Documenti PDF:
italian.PdfProgId=Documento PDF
italian.PdfDescription=Legge e annota i PDF con la penna del Surface.
italian.RunApp=Avvia Dream Books
italian.RunDefaultApps=Scegli Dream Books come app predefinita per i PDF (si apre la pagina di Windows)
italian.UpdateAsk=È installata la versione %1 di Dream Books.%nAggiornare alla versione %2?
italian.ReinstallAsk=La versione %1 di Dream Books è già installata.%nReinstallarla?
italian.DowngradeRefused=È già installata una versione più recente di Dream Books (%1).%nQuesta procedura non la sostituisce.
italian.DeleteDataAsk=Eliminare anche le impostazioni e le posizioni di lettura di Dream Books?%n%nI tuoi file PDF non vengono toccati.

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktop}"; GroupDescription: "{cm:TaskIcons}"; Flags: unchecked
Name: "pdfassoc"; Description: "{cm:TaskPdf}"; GroupDescription: "{cm:TaskPdfGroup}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; "Apri con" and Settings > Default apps list Dream Books (Windows does not let an app make itself the default).
Root: HKCU; Subkey: "Software\Classes\DreamBooks.Pdf"; ValueType: string; ValueName: ""; ValueData: "{cm:PdfProgId}"; Flags: uninsdeletekey; Tasks: pdfassoc
Root: HKCU; Subkey: "Software\Classes\DreamBooks.Pdf\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Tasks: pdfassoc
Root: HKCU; Subkey: "Software\Classes\DreamBooks.Pdf\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: pdfassoc
Root: HKCU; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "DreamBooks.Pdf"; ValueData: ""; Flags: uninsdeletevalue; Tasks: pdfassoc
Root: HKCU; Subkey: "Software\DreamBooks"; Flags: uninsdeletekeyifempty; Tasks: pdfassoc
Root: HKCU; Subkey: "Software\DreamBooks\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: pdfassoc
Root: HKCU; Subkey: "Software\DreamBooks\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "{cm:PdfDescription}"; Tasks: pdfassoc
Root: HKCU; Subkey: "Software\DreamBooks\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "DreamBooks.Pdf"; Tasks: pdfassoc
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\DreamBooks\Capabilities"; Flags: uninsdeletevalue; Tasks: pdfassoc

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:RunApp}"; Flags: nowait postinstall skipifsilent
Filename: "ms-settings:defaultapps"; Description: "{cm:RunDefaultApps}"; Flags: shellexec nowait postinstall skipifsilent; Tasks: pdfassoc

[Code]
procedure SHChangeNotify(wEventId, uFlags, dwItem1, dwItem2: Integer);
  external 'SHChangeNotify@shell32.dll stdcall';

// The version of the install already on this user's PC, or '' (read from Inno's own uninstall entry).
function InstalledVersion(): String;
begin
  if not RegQueryStringValue(HKCU,
      ExpandConstant('Software\Microsoft\Windows\CurrentVersion\Uninstall\{#emit SetupSetting("AppId")}_is1'),
      'DisplayVersion', Result) then
    Result := '';
end;

// Update, reinstall and downgrade (never replaces a newer version with this older one).
function InitializeSetup(): Boolean;
var
  Installed: String;
  InstalledPacked, ThisPacked: Int64;
  Order: Integer;
begin
  Result := True;
  Installed := InstalledVersion();
  if Installed = '' then
    Exit;
  if not (StrToVersion(Installed, InstalledPacked) and StrToVersion('{#AppVersion}', ThisPacked)) then
    Exit; // an unreadable version: treat it as an update
  Order := ComparePackedVersion(InstalledPacked, ThisPacked);
  if Order < 0 then
    Result := MsgBox(FmtMessage(CustomMessage('UpdateAsk'), [Installed, '{#AppVersion}']), mbConfirmation, MB_YESNO) = IDYES
  else if Order = 0 then
    Result := MsgBox(FmtMessage(CustomMessage('ReinstallAsk'), ['{#AppVersion}']), mbConfirmation, MB_YESNO) = IDYES
  else
  begin
    MsgBox(FmtMessage(CustomMessage('DowngradeRefused'), [Installed]), mbError, MB_OK);
    Result := False;
  end;
end;

// The user's settings, reading positions and recovery journals stay unless asked (default: keep).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if MsgBox(CustomMessage('DeleteDataAsk'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\DreamBooks'), True, True, True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // Tell Explorer the file associations changed, so "Apri con" shows Dream Books at once.
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('pdfassoc') then
    SHChangeNotify($08000000, 0, 0, 0); // SHCNE_ASSOCCHANGED
end;
