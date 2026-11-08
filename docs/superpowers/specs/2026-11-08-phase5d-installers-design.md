# Phase 5d: per-user installers (distribution row of section 1)

Goal: Fausto can hand a friend one `.exe` that installs Dream Books on a Surface without administrator rights, tells the
friend what is being installed, asks where the icons go and whether Dream Books should open PDFs, and updates an older
install when a newer `.exe` is run. Distribution is private, so the installers are not signed (SmartScreen warns the
first time; the welcome page says so).

Non-goals: code signing, MSIX, winget, an update check over the network (NFR-40: the app makes no network calls), a
silent-update service. Anti-goal: an installer that needs administrator rights, or that deletes the user's settings,
reading positions or recovery journals without asking.

## What Fausto decided

- Two separate EXE installers, x64 and ARM64, per user, no administrator rights (decision of 07/11/2026).
- The wizard is in Italian and explains what is being installed.
- It asks where the icons go besides the Start menu.
- It asks whether Dream Books becomes the default PDF app.
- To update, a newer `.exe` is distributed; when it finds an older install it asks whether to update.
- Inno Setup is the tool (installed per user on the dev Surface, 6.7.3). Nobody who installs needs it.
- Only x64 can be tried on a real device (no friend has an ARM64 Surface); the ARM64 installer is built and its
  contents checked, not run (NFR-21).

## Design (my calls are marked "call")

**Files.** `installer/DreamBooks.iss` (the Inno script, one script for both architectures, chosen by a define),
`installer/build.sh` (bash, not PowerShell: scripts are blocked by the execution policy on this machine; publishes
self-contained for the architecture, then runs `ISCC`), `installer/bump-version.sh`, `installer/welcome.it.txt`
(the Italian text of the first page). Output goes to `artifacts/installers/` (git-ignored):
`DreamBooks-<version>-x64-setup.exe` and `DreamBooks-<version>-arm64-setup.exe`.

**Version (call).** One source: `<Version>` in `Directory.Build.props`. `build.sh` reads it and passes it to `ISCC`
(`/DAppVersion=`); the exe, the installer, "App installate" and the line at the bottom of the app's settings
(`AppVersion.Current`, read from the build) show the same number. First value `0.5.0` (phase 5);
`bump-version.sh major|minor|patch|X.Y.Z` raises it.

**Name in Windows (call).** The "Apri con" list and Default apps show the exe's file description, set in the App csproj
(`AssemblyTitle`, `Product` = "Dream Books"); the installer also registers `FriendlyAppName`. Changing the working
name later means changing those two places and `AppName` in the script.

**Per user.** `PrivilegesRequired=lowest`, install under `{autopf}` (resolves to `%LocalAppData%\Programs\Dream Books`
without admin). A fixed `AppId` GUID identifies the app across versions. Uninstall appears in Windows "App installate".

**Wizard pages.** Language Italian. Welcome page: what Dream Books is (reading and annotating PDFs with the pen),
version, that it works offline and sends nothing, that the installer is not signed so Windows may warn. Components
page is not shown. A Tasks page offers: "Icona sul desktop" (call: unchecked by default) and "Apri i file PDF con Dream
Books" (call: checked). The Start menu entry is always created. Finish page: "Avvia Dream Books", and, if the PDF task
was chosen, "Scegli Dream Books come app predefinita per i PDF".

**Default PDF app (call).** Windows does not let an app make itself the default. With the PDF task the installer
registers, under `HKCU\Software\Classes` and `HKCU\Software\RegisteredApplications`, a ProgID, the `.pdf` entry in
`OpenWithProgids` and the app's Capabilities. That makes Dream Books appear in "Apri con" and in Settings → Default
apps. The finish-page choice opens that Settings page (`ms-settings:defaultapps`), and the final click is the user's.
The app already opens a file passed as argument (FR-01). Uninstall removes all of these keys.

**Update.** The installer reads the installed version from its uninstall key. Same or older installed: for an older
one it asks "È installata la versione X. Aggiornare alla Y?" (No ends the setup); the same version asks to reinstall
(call); a newer installed version than the installer is refused with a message (no downgrades, call). An update keeps
the install folder and the choices of the first install as defaults, replaces the files, and leaves `LocalAppData`
data alone. A running Dream Books is closed first (Inno's `CloseApplications`), with the user's consent.

**User data on uninstall (call).** The files go; the settings, reading positions and recovery journals under
`%LocalAppData%\DreamBooks` stay unless the user ticks "Elimina anche impostazioni e posizioni di lettura" (default off).
PDFs are never touched.

**Texts.** The wizard's own strings are Inno's Italian translation plus our custom messages in the script's
`[CustomMessages]` section (the one place for installer text; the app's UI text still lives in `.resx`).

## Checks

An installer cannot be unit tested. Automatic: `build.ps1` fails if publish or `ISCC` fails; for each architecture it
checks that the output exists and that the file's version resource matches the csproj version. Existing rules still
apply: `dotnet build` with no new warnings, `dotnet test` green, the ARM64 publish for the installer is built to
`artifacts/` (not into `bin/`).

Manual checklist on the Surface (x64): install on a clean user (or after uninstalling); read the welcome text; icons as
asked; "Apri con" lists Dream Books; the Settings page opens; double-click a PDF opens it; run a newer installer over it
(update prompt, settings kept); run an older one (refused); uninstall with and without the data box.

## Open for Fausto

None blocking. Calls to confirm: version `0.5.0`; desktop icon unchecked and PDF task checked by default; reinstalling the
same version allowed; downgrade refused; user data kept by default.
