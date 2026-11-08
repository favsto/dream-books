# Phase 5d Installers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (inline) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two per-user, unsigned, Italian Inno Setup installers (x64, ARM64) with a welcome text, icon choices, PDF-default registration, update/downgrade handling and a user-data choice on uninstall.

**Architecture:** One Inno script (`installer/DreamBooks.iss`) parameterised by architecture and version; `installer/build.ps1` publishes self-contained, compiles the script and verifies the output. The version has a single source, the app csproj.

**Tech Stack:** Inno Setup 6.7.3 (`%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`, installed), PowerShell, .NET 10 publish.

**Spec:** `docs/superpowers/specs/2026-11-08-phase5d-installers-design.md`

## Global Constraints

- Per user, no administrator rights: `PrivilegesRequired=lowest`; nothing written under HKLM or Program Files.
- Unsigned. No network calls anywhere (NFR-40). Never touch the user's PDFs.
- Installer texts are Italian and live in the script's `[CustomMessages]`; repository files (script, comments, commit messages) are English.
- Output in `artifacts/installers/` (git-ignored); never into `bin/`.
- `dotnet build` with no new warnings and `dotnet test` green before done (CLAUDE.md).
- Inno cannot be unit tested: each task's gate is `build.ps1` succeeding plus the checks it runs. Real behaviour is verified by Fausto's manual checklist (Task 5).

## Review Focus

- Update over an existing install must not lose `%LocalAppData%\DreamBooks` data.
- A running Dream Books must not make the update fail silently.
- Uninstall must remove every registry key the PDF task wrote, and nothing else.
- A path with spaces or accents (Italian user names) must work for install folder and the "Apri con" command line.
- The same installer run twice in a row must end in a clean, working install.

---

### Task 1: Version, build script, minimal installer

**Files:**
- Modify: `src/DreamBooks.App/DreamBooks.App.csproj` (add `<Version>0.5.0</Version>`)
- Create: `installer/DreamBooks.iss`, `installer/build.ps1`

**Interfaces:**
- Produces: `build.ps1 -Arch x64|arm64` (default x64) → `artifacts/installers/DreamBooks-<version>-<arch>-setup.exe`; the script defines `AppVersion`, `Arch`, `SourceDir` (`/D` from the build script).

- [ ] **Step 1:** In `build.ps1`: read `<Version>` from the csproj; run `dotnet publish src/DreamBooks.App -c Release -r win-<arch> --self-contained -o artifacts/publish/<arch>`; run `ISCC /DAppVersion=… /DArch=… /DSourceDir=… installer/DreamBooks.iss`; fail (non-zero exit) on any step failing; afterwards check the output exists and its `ProductVersion` starts with the csproj version.
- [ ] **Step 2:** Run `./installer/build.ps1` before the script exists. Expected: FAIL (missing `DreamBooks.iss`).
- [ ] **Step 3:** Write the minimal `DreamBooks.iss`: `[Setup]` with fixed `AppId` GUID, `AppName=Dream Books`, `AppVersion={#AppVersion}`, `PrivilegesRequired=lowest`, `DefaultDirName={autopf}\Dream Books`, `OutputDir=..\artifacts\installers`, `OutputBaseFilename=DreamBooks-{#AppVersion}-{#Arch}-setup`, `ArchitecturesAllowed`/`ArchitecturesInstallIn64BitMode` per `Arch`, `Compression=lzma2`, `WizardStyle=modern`, `SetupIconFile` = `src\DreamBooks.App\Assets\app-icon.ico`, `UninstallDisplayIcon`; `[Languages]` Italian (`compiler:Languages\Italian.isl`); `[Files]` `{#SourceDir}\*` recursive; `[Icons]` Start menu entry.
- [ ] **Step 4:** Run `./installer/build.ps1`. Expected: x64 setup exe built, version check passes.
- [ ] **Step 5:** Commit `feat: per-user installer skeleton and build script (FR-01 groundwork, NFR-21)`.

### Task 2: Welcome text, icon choices, PDF registration

**Files:** Modify `installer/DreamBooks.iss`; Create `installer/welcome.it.txt`.

- [ ] **Step 1:** Add to `build.ps1` a check that fails when the script lacks any of: `[Tasks]` entries `desktopicon` and `pdfassoc`, the `HKCU` PDF keys (below), and the `welcome.it.txt` reference. (RED: run it, expect FAIL.)
- [ ] **Step 2:** `welcome.it.txt` (Italian, short): what Dream Books is, that it works offline and sends nothing, that the installer is not signed so Windows SmartScreen may warn ("Maggiori informazioni" → "Esegui comunque"). Shown with `InfoBeforeFile`.
- [ ] **Step 3:** `[Tasks]`: `desktopicon` (unchecked), `pdfassoc` (checked, custom message "Apri i file PDF con Dream Books"); `[Icons]` desktop entry when `desktopicon`.
- [ ] **Step 4:** `[Registry]` (all `HKCU`, flags `uninsdeletekey` where the key is ours): ProgID `DreamBooks.Pdf` under `Software\Classes` with `shell\open\command` = `"{app}\DreamBooks.App.exe" "%1"` and a `DefaultIcon`; `Software\Classes\.pdf\OpenWithProgids` value `DreamBooks.Pdf` (`uninsdeletevalue`); `Software\DreamBooks\Capabilities` (ApplicationName, ApplicationDescription, `FileAssociations\.pdf`=`DreamBooks.Pdf`); `Software\RegisteredApplications` value `Dream Books`. All under task `pdfassoc`.
- [ ] **Step 5:** `[Run]` on the finish page: "Avvia Dream Books" (`postinstall nowait`), and, only for `pdfassoc`, "Scegli Dream Books come app predefinita per i PDF" running `ms-settings:defaultapps` via `shellexec`.
- [ ] **Step 6:** `[Code]` calls `SHChangeNotify` (assoc changed) after install when `pdfassoc`. Run `./installer/build.ps1`. Expected: PASS. Commit `feat: installer welcome text, icon choices and PDF registration (FR-01)`.

### Task 3: Update, reinstall and downgrade handling

**Files:** Modify `installer/DreamBooks.iss`.

**Interfaces:**
- Produces `function InstalledVersion(): String` (reads `DisplayVersion` of `Software\Microsoft\Windows\CurrentVersion\Uninstall\<AppId>_is1` in HKCU; empty if none); `InitializeSetup(): Boolean` using `StrToVersion` and `ComparePackedVersion`.

- [ ] **Step 1:** Add to the build script's script-content check: `InitializeSetup`, `InstalledVersion`, `CloseApplications=yes`. Run: FAIL (RED).
- [ ] **Step 2:** `InitializeSetup`: none installed → continue. Installed older → `MsgBox` "È installata la versione X. Aggiornare alla Y?" (Yes continue, No `Result := False`). Same → "La versione Y è già installata. Reinstallarla?". Installed newer → message "È già installata una versione più recente (X). Questa procedura non la sostituisce." and `Result := False`. Texts from `[CustomMessages]`, formatted with `FmtMessage`.
- [ ] **Step 3:** `[Setup]`: `CloseApplications=yes`, `RestartApplications=no`, `UsePreviousAppDir=yes`, `UsePreviousTasks=yes`; no data under `{localappdata}\DreamBooks` appears in `[Files]`, `[InstallDelete]` or `[UninstallDelete]` yet (the check fails if it does).
- [ ] **Step 4:** `./installer/build.ps1`: PASS. Commit `feat: installer asks before updating and refuses downgrades`.

### Task 4: Uninstall and user data

**Files:** Modify `installer/DreamBooks.iss`.

- [ ] **Step 1:** Add the check: script contains `CurUninstallStepChanged` and the deletion is guarded by the user's answer. RED first.
- [ ] **Step 2:** In `CurUninstallStepChanged(usUninstall)`: ask (default No) "Eliminare anche le impostazioni e le posizioni di lettura?"; on Yes `DelTree(ExpandConstant('{localappdata}\DreamBooks'), True, True, True)`. Never touches anything else (PDF files untouched).
- [ ] **Step 3:** `./installer/build.ps1`: PASS. Commit `feat: uninstall keeps user data unless asked (NFR-10 spirit)`.

### Task 5: ARM64, docs, checklist, final checks

**Files:** Modify `docs/requirements.md` (5d status), `CLAUDE.md` (Commands table: installer build; project structure `installer/`).

- [ ] **Step 1:** `./installer/build.ps1 -Arch arm64`: both installers exist, ARM64 one is built (not run). Inspect that the ARM64 publish folder holds an ARM64 `DreamBooks.App.exe` and the `pdfium` native is the ARM64 one.
- [ ] **Step 2:** `dotnet build` (0 warnings), `dotnet format --verify-no-changes`, `dotnet test` (run Pdf tests apart if the machine is under load).
- [ ] **Step 3:** Update `docs/requirements.md` (5d: built, **in prova** on the Surface) and `CLAUDE.md` (command row and structure line). Commit `docs: phase 5d installers, commands and status`.
- [ ] **Step 4:** Give Fausto the Italian manual checklist from the design note and wait for his results; do not merge before.
