# Phase 4b Implementation Plan: start experience, settings, export, movable toolbar

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Branch: `feature/phase4b-start-settings` from `main`; do not merge until Fausto's device test passes.

**Goal:** Give the app its own face (icon, splash), a start screen with recent documents, a settings panel, two exports (flattened PDF, page PNG) and a toolbar that can sit on any edge.

**Architecture:** Pure models in `DreamBooks.Core` (recent list, toolbar edge snapping), file persistence in `DreamBooks.Pdf` (recents file, cover cache, flattened export), view models and WPF views in `DreamBooks.App`, all in the 4a surface language (`FloatingSurface`, theme tokens, `.resx`). The start screen is a view over the document area shown while `Session is null`; the splash is a separate lightweight window shown before the heavy start-up.

**Tech Stack:** .NET 10, WPF + WPF UI 4.3, CommunityToolkit.Mvvm, PDFium (own P/Invoke), xUnit. No new NuGet package.

**Spec:** `docs/superpowers/specs/2026-11-06-phase4b-start-settings-design.md` (read it first; "Calls added after the builder check" included). Mockups: `.superpowers/brainstorm/4b-start-mockups.html`.

## Global Constraints

- Code, comments, names, commits, docs for Claude in English; user-visible text Italian in `src/DreamBooks.App/Resources/Strings.resx` (+ `Strings.Designer.cs`), never literals.
- `DreamBooks.Core` never references WPF, PDFium or PDFsharp. TDD mandatory in Core and Pdf; view models unit-tested in App; visuals by the manual checklist.
- Tokens (radii, sizes, colours, durations) only in `Theme.xaml` / `Theme.Light.xaml` / `Theme.Dark.xaml`; every new key exists in both themes where it is a colour (`ThemeResourcesTests` enforces it and bans literals in views).
- NFR-32: opaque brushes with Windows transparency off, no animation with animations off. 40 × 40 px minimum touch target. `AutomationProperties.Name` on every command (NFR-31).
- Pen rules unchanged: floating surfaces never take the pen outside their bounds; the pen presses controls inside them; **fingers never draw, in no setting**.
- Settings file never contains document paths or content (FR-110). The recents list is a separate file `%LOCALAPPDATA%\DreamBooks\recent.json`, versioned (`"v":1`), written atomically, never throws. Never log document content or paths (NFR-12). No network calls (NFR-40).
- Saving/exporting writes a temporary file, verifies, then replaces; the open document and the original file are never touched by an export (NFR-10).
- Ask before any NuGet package. Pen/visual results need Fausto's on-device test.
- Before each commit: `dotnet build` 0 warnings, `dotnet test` green (the App suite takes ~5 min: run the filtered tests per task, the whole suite at the end), `dotnet format --verify-no-changes`. Commit trailer: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`; messages cite requirement IDs.
- Never run the WPF app (windows shown only inside STA tests that already do so, like `MaximiseTests`).

## Review Focus

1. A recent whose file moved, was deleted, or sits on a disconnected/slow network drive: card says "non trovato", removable, and the check never blocks the UI thread (Task 4, Task 6).
2. `recent.json` missing, corrupt, of a newer version, or with 1000 entries: defaults/ cap 20, no crash (Task 3).
3. Export: destination exists / user cancels / disk full / encrypted source: original untouched, nothing left behind, notice shown (Task 11).
4. Start-up with a file argument that cannot be opened, or a restore that fails: the splash still closes and the start screen shows with a notice (Task 8).
5. Toolbar on an edge in a very small window, and a drop of a non-PDF file onto the window (Tasks 10, 6).

---

### Task 1: App icon assets

**Files:**
- Create: `src/DreamBooks.App/Assets/app-icon.png` (transparent, 256 × 256), `src/DreamBooks.App/Assets/app-icon.ico` (16, 24, 32, 48, 64, 128, 256), `tools/make-icon.ps1` (one-off, kept for replacing the source)
- Modify: `src/DreamBooks.App/DreamBooks.App.csproj` (`<ApplicationIcon>`, `Resource Include` for the png), `src/DreamBooks.App/MainWindow.xaml` (`Icon`)
- Test: `tests/DreamBooks.App.Tests/AppIconTests.cs`

**Interfaces:**
- Produces: pack URI `pack://application:,,,/DreamBooks.App;component/Assets/app-icon.png` (used by Task 7 splash and Task 5 start screen).

- [ ] **Step 1: Write failing tests** `Icon_png_has_transparent_corners_and_an_opaque_centre` (decode the resource: pixel (0,0) alpha 0; centre alpha 255) and `Icon_ico_holds_all_the_sizes` (ICO header count 7 and the size list above).
- [ ] **Step 2: Run** `dotnet test tests/DreamBooks.App.Tests --filter AppIconTests`. Expected: FAIL (resource missing).
- [ ] **Step 3: Implement** `tools/make-icon.ps1` (PowerShell + `System.Drawing`): read `extra/dream-book-icon.png`, flood-fill the white background from the four corners to transparent with a small tolerance and soften the edge, resize to 256, write the png, then write the ico with PNG-compressed frames. Run it once; commit the outputs. Remove `extra/` use from the build (keep the original there).
- [ ] **Step 4: Run** the same filter. Expected: PASS. Also `dotnet build`: the exe carries the icon.
- [ ] **Step 5: Commit** `feat: app icon with transparent background and multi-size ico (4b)`.

### Task 2: Recent files model (Core)

**Files:**
- Create: `src/DreamBooks.Core/Recent/RecentFile.cs`, `src/DreamBooks.Core/Recent/RecentFilesList.cs`
- Test: `tests/DreamBooks.Core.Tests/Recent/RecentFilesListTests.cs`

**Interfaces:**
- Produces: `public sealed record RecentFile(string Path, DateTime LastOpenedUtc, int Page, int PageCount);` and `public sealed class RecentFilesList` with `public const int MaxCount = 20; public RecentFilesList(IEnumerable<RecentFile>? items = null); public IReadOnlyList<RecentFile> Items { get; } /* newest first */; public void Touch(RecentFile file); public bool Remove(string path); public void UpdateProgress(string path, int page, int pageCount);`. Paths compare case-insensitively on the full path.

- [ ] **Step 1: Write failing tests**: `Touch_puts_a_file_first_and_replaces_the_same_path`, `Touch_ignores_case_of_the_path`, `The_list_keeps_the_newest_20`, `Constructor_trims_to_20_and_drops_blank_paths_and_duplicates`, `Remove_returns_false_for_an_unknown_path`, `UpdateProgress_changes_page_without_reordering`, `Negative_or_invalid_page_counts_are_clamped` (Page within 0..PageCount-1, PageCount ≥ 1).
- [ ] **Step 2: Run** `dotnet test tests/DreamBooks.Core.Tests --filter RecentFilesListTests`. Expected: FAIL (types missing).
- [ ] **Step 3: Implement** the two types as specified.
- [ ] **Step 4: Run** the same filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat: recent files model, newest first, at most 20 (FR-03)`.

### Task 3: Recents file and cover cache (Pdf)

**Files:**
- Create: `src/DreamBooks.Pdf/RecentFilesStore.cs`, `src/DreamBooks.Pdf/CoverStore.cs`
- Test: `tests/DreamBooks.Pdf.Tests/RecentFilesStoreTests.cs`, `tests/DreamBooks.Pdf.Tests/CoverStoreTests.cs`

**Interfaces:**
- Consumes: `RecentFilesList`, `RecentFile` (Task 2); `RecoveryStore.DocumentKey(string path)`.
- Produces: `RecentFilesStore(string rootDirectory)` with `RecentFilesList Load()` (never throws), `void Save(RecentFilesList list)` (atomic temp+replace like `ViewPositionStore`; failures through `event EventHandler<Exception>? Failed`), `static string DefaultRootDirectory()` (same folder as `AppSettingsStore`). `CoverStore(string rootDirectory, long maxBytes = 8L * 1024 * 1024)` with `byte[]? TryLoad(string documentPath)`, `void Save(string documentPath, byte[] png)` (then deletes oldest files beyond `maxBytes`), `void Delete(string documentPath)`, `void Clear()`; files `covers\<DocumentKey>.png`; nothing throws.

- [ ] **Step 1: Write failing tests**: round trip; missing/corrupt/`"v":2`/truncated file → empty list; 1000 entries on disk → 20; file holds format `{"v":1,"items":[{"path","opened","page","pages"}]}`; **the settings file never contains a path after recents are saved** (use `AppSettingsStore` + `RecentFilesStore` in one directory); `Save` failure (read-only directory) raises `Failed` and does not throw; orphan `.tmp` swept at load; cover store trims to the byte budget oldest first and `TryLoad` of a missing key returns null.
- [ ] **Step 2: Run** `dotnet test tests/DreamBooks.Pdf.Tests --filter "RecentFilesStoreTests|CoverStoreTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** both classes following `ViewPositionStore` and `AppSettingsStore` (versioned, atomic, `Failed` event, ASCII-safe JSON via `System.Text.Json`).
- [ ] **Step 4: Run** the same filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat: recents file and cover cache in LocalAppData, separate from settings (FR-03, FR-110)`.

### Task 4: Recents service, relative time, start view model

**Files:**
- Create: `src/DreamBooks.App/Services/RecentFilesService.cs`, `src/DreamBooks.App/Services/RelativeTimeFormatter.cs`, `src/DreamBooks.App/ViewModels/StartViewModel.cs`, `src/DreamBooks.App/ViewModels/RecentCardViewModel.cs`
- Modify: `Strings.resx` / `Strings.Designer.cs` (keys: `StartResume` "Riprendi a leggere", `StartOpenPdf` "Apri PDF…", `StartMore` "Altri…", `StartMissing` "non trovato", `StartRemove` "Rimuovi dall'elenco", `WhenToday` "oggi", `WhenYesterday` "ieri", `WhenDaysFormat` "{0} giorni fa", `WhenWeeksFormat` "{0} settimane fa", `WhenMonthsFormat` "{0} mesi fa", `StartPageFormat` "pag. {0}")
- Test: `tests/DreamBooks.App.Tests/RelativeTimeFormatterTests.cs`, `RecentFilesServiceTests.cs`, `StartViewModelTests.cs`

**Interfaces:**
- Consumes: `RecentFilesStore`, `RecentFilesList`, `CoverStore` (Tasks 2-3); `IDelayScheduler` (existing).
- Produces:
  - `internal static class RelativeTimeFormatter { public static string Format(DateTime openedUtc, DateTime nowUtc); }`
  - `internal sealed class RecentFilesService(RecentFilesStore store, Func<string, bool> fileExists, IDelayScheduler scheduler)`: `IReadOnlyList<RecentEntry> Entries`, `event EventHandler? Changed`, `void Opened(string path, int page, int pageCount)`, `void UpdateProgress(string path, int page, int pageCount)`, `void Remove(string path)`, `Task RefreshAvailabilityAsync(CancellationToken ct)` (existence checked on the thread pool, never on the UI thread), `void Load()`. Saves debounced like `SettingsService` (500 ms).
  - `internal sealed record RecentEntry(string Path, string FileName, DateTime LastOpenedUtc, int Page, int PageCount, bool? Exists);` (`Exists` null until checked)
  - `StartViewModel`: `IReadOnlyList<RecentCardViewModel> Cards`, `bool HasRecents`, `bool ShowMore`, `IAsyncRelayCommand OpenPdfCommand`, `IAsyncRelayCommand<RecentCardViewModel> OpenRecentCommand`, `IRelayCommand<RecentCardViewModel> RemoveCommand`. `RecentCardViewModel`: `Path`, `Title` (file name without extension), `WhenText`, `PageText`, `double Progress` (0..1, `(Page+1)/PageCount`), `bool IsMissing`, `BitmapSource? Cover` (settable, raises change).

- [ ] **Step 1: Write failing tests**: formatter table (same day → "oggi", 1 day "ieri", 3 → "3 giorni fa", 14 → "2 settimane fa", 60 → "2 mesi fa", future date → "oggi"); service: `Opened` puts the file first and saves after 500 ms, `Remove` saves, a missing file gets `Exists == false` after `RefreshAvailabilityAsync` and that call does not run `fileExists` on the calling thread (assert thread id), a throwing `fileExists` counts as missing; view model: cards follow `Changed`, `IsMissing` card's command removes instead of opening and shows `StartMissing`, `Progress` for page 130 of 400, 20 recents → `ShowMore` true when more than the fit count passed to the ctor.
- [ ] **Step 2: Run** `dotnet test tests/DreamBooks.App.Tests --filter "RelativeTimeFormatterTests|RecentFilesServiceTests|StartViewModelTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** the types and resx keys.
- [ ] **Step 4: Run** the same filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat: recents service, relative dates and start view model (FR-03)`.

### Task 5: Open/close integration in MainViewModel

**Files:**
- Modify: `src/DreamBooks.App/ViewModels/MainViewModel.cs` (open path ~line 691-707, restore ~473), `src/DreamBooks.App/Services/IFileDialogService.cs` / `FileDialogService.cs` only if a close needs a question (reuse `AskSaveChanges`), `Strings.resx` (`CloseDocument` "Chiudi documento")
- Test: `tests/DreamBooks.App.Tests/MainViewModelRecentsTests.cs`

**Interfaces:**
- Consumes: `RecentFilesService` (Task 4); `ViewPositionStore` (existing, gives the last page).
- Produces on `MainViewModel`: `RecentFilesService? Recents { get; set; }`, `StartViewModel? Start`, `bool IsStartScreenVisible` (true when `Session is null` and no open/restore is in progress; raises `PropertyChanged`), `IAsyncRelayCommand CloseDocumentCommand` (asks `AskSaveChanges` when dirty; cancel keeps the document; then releases the document, saves the position, clears `Session`), `public Task OpenRecentAsync(string path, CancellationToken ct = default)` (public wrapper over `OpenPathAsync`), `public Task OpenStartupFileAsync(string path, CancellationToken ct = default)` (like `OpenRecentAsync` but a failure shows the usual error and leaves the start screen).

- [ ] **Step 1: Write failing tests** (use the existing fakes `FakeDocumentWriter`, `FakeFileDialogService`, `FakePageSource`): a successful open calls `Recents.Opened(path, page, pageCount)`; Save As adds the new path; closing the document (clean) shows the start screen and records the last page; closing dirty with Cancel keeps the document; a failed `OpenStartupFileAsync` leaves `IsStartScreenVisible` true; `TryRestoreLastSessionAsync` success keeps the start screen hidden and failure shows it.
- [ ] **Step 2: Run** `dotnet test tests/DreamBooks.App.Tests --filter MainViewModelRecentsTests`. Expected: FAIL.
- [ ] **Step 3: Implement** as specified; the recents are updated on open and when the position is saved (close, exit).
- [ ] **Step 4: Run** the same filter plus `MainViewModelTests`. Expected: PASS.
- [ ] **Step 5: Commit** `feat: opening and closing documents keeps the recents and shows the start screen (FR-03, FR-04)`.

### Task 6: Start screen view (variant B), drop to open, covers

**Files:**
- Create: `src/DreamBooks.App/Views/StartView.xaml(.cs)`, `src/DreamBooks.App/Views/RecentCard.xaml(.cs)`, `src/DreamBooks.App/Services/CoverService.cs`
- Modify: `MainWindow.xaml(.cs)` (host `StartView` over the document area, `AllowDrop`, drop handler), `Theme.xaml` / `Theme.Light.xaml` / `Theme.Dark.xaml` (card width/height, cover placeholder brush, progress bar brush, spacing tokens), `ThemeResourcesTests` key list
- Test: `tests/DreamBooks.App.Tests/StartViewTests.cs`, `CoverServiceTests.cs`

**Interfaces:**
- Consumes: `StartViewModel`, `RecentCardViewModel` (Task 4); `MainViewModel.IsStartScreenVisible/OpenRecentAsync` (Task 5); `CoverStore` (Task 3); the existing thumbnail render path (`PageBitmapCoordinator.RenderThumbnailAsync`, low priority) for page 1.
- Produces: `internal sealed class CoverService(CoverStore store, Func<string, CancellationToken, Task<BitmapSource?>> renderFirstPage)`: `Task<BitmapSource?> GetAsync(string documentPath, CancellationToken ct)` (cache hit decodes the png; a miss for a file that is not open returns null: **no background rendering of unopened files**); `Task StoreAsync(string documentPath, BitmapSource page1, CancellationToken ct)` called when a document is opened (page-1 bitmap without ink, encoded png). `MainWindow.OpenDroppedFile(string[] paths)`: opens the first `.pdf`, ignores anything else and shows `NoticeBar` "Questo non è un PDF" (`StartNotAPdf`).

- [ ] **Step 1: Write failing tests**: `StartView` shows one card per recent in the order given with title, when text, progress bar width = `Progress`; a missing card shows `StartMissing` and the remove button; the cover placeholder shows until `Cover` arrives and the card does not change size when it does (no flicker); the view is collapsed while a document is open; cards ≥ 40 px targets, named for automation; the pen is not hit-test blocked outside the view's surfaces (same rule as other floating views: only card surfaces hit-test); `CoverService`: store then get round-trips, miss returns null without calling `renderFirstPage`; drop of `a.txt` only → notice and no open, drop of `a.pdf` → `OpenRecentAsync`.
- [ ] **Step 2: Run** `dotnet test tests/DreamBooks.App.Tests --filter "StartViewTests|CoverServiceTests|ThemeResourcesTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** the view from the mockup (variant B in `4b-start-mockups.html`), cards as `FloatingSurface`s, "Altri…" card opening the full list as a simple list in the same surface; tokens in the theme; strings in `.resx`; covers stored after a successful open.
- [ ] **Step 4: Run** the same filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat: start screen with recent covers and drop to open (FR-03, UI-03)`.

### Task 7: Menu entries: File recenti and Chiudi documento

**Files:**
- Modify: `src/DreamBooks.App/Views/MainMenuButton.xaml(.cs)` (`RecentItem` enabled, a recent list in the panel using the same cards-as-rows data, new `CloseItem`), `Strings.resx`, `MainWindow.xaml(.cs)` (wire commands)
- Test: extend `tests/DreamBooks.App.Tests/MainMenuTests.cs`, `DebugPanelReplacementTests.cs` if it lists entries

**Interfaces:**
- Consumes: `MainViewModel.Recents/Entries`, `OpenRecentAsync`, `CloseDocumentCommand` (Task 5). Produces: menu items named for automation; no new public API.

- [ ] **Step 1: Write failing tests**: `RecentItem` is enabled when there are recents and shows up to 8 rows (the menu is small; the full list is the start screen) **(call)**; choosing a row opens it and closes the menu; `CloseItem` enabled only with a document, runs `CloseDocumentCommand`; both have Italian automation names and keyboard access; pen rules as for other menu entries.
- [ ] **Step 2: Run** `dotnet test tests/DreamBooks.App.Tests --filter "MainMenuTests|DebugPanelReplacementTests"`. Expected: FAIL.
- [ ] **Step 3: Implement**.
- [ ] **Step 4: Run** the same filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat: menu entries for recent files and closing the document (UI-03)`.

### Task 8: Splash and start-up sequence

**Files:**
- Create: `src/DreamBooks.App/Views/SplashWindow.xaml(.cs)`
- Modify: `src/DreamBooks.App/App.xaml.cs` (`OnStartup`: show the splash first, create everything else, decide start-up document, close the splash), `MainWindow.xaml.cs` (expose `Task StartupCompleted`), `Strings.resx` (`SplashTagline` "Leggi. Annota. Sogna."), theme tokens for the splash
- Test: `tests/DreamBooks.App.Tests/SplashStartupTests.cs`

**Interfaces:**
- Consumes: pack URI of the icon (Task 1); `MainViewModel.OpenStartupFileAsync`, `TryRestoreLastSessionAsync` (Task 5).
- Produces: `internal sealed class SplashWindow : Window` with `bool ReducedMotion { get; set; }`; `internal static class StartupPlan { public static StartupAction Decide(string? commandLineFile, bool restorable); }` returning `OpenFile | Restore | StartScreen` (file argument wins, then restore, else start screen); `MainWindow.StartupCompleted : Task` completes when the chosen action has finished (success or failure) and the window is shown.

- [ ] **Step 1: Write failing tests**: `StartupPlan.Decide` table (file+restorable → OpenFile; none+restorable → Restore; none → StartScreen); the splash shows icon, "Dream Books", the tagline, an indeterminate indicator that is static with `ReducedMotion`; it has no minimum display time (closes as soon as `StartupCompleted` completes: test with a completed task); **a file argument that cannot be opened still completes `StartupCompleted` and leaves the start screen with the usual error notice**.
- [ ] **Step 2: Run** `dotnet test tests/DreamBooks.App.Tests --filter SplashStartupTests`. Expected: FAIL.
- [ ] **Step 3: Implement**: the splash is a borderless centred window with a fixed dark look, created and shown before the theme service, settings and main window; `App.OnStartup` reads `e.Args` for one file path; the main window is shown when ready and the splash closes on its first render. Technique inside these limits is the builder's.
- [ ] **Step 4: Run** the same filter. Expected: PASS. Run the whole App suite once.
- [ ] **Step 5: Commit** `feat: splash and start-up order: file argument, restore, start screen (NFR-01)`.

### Task 9: Settings panel

**Files:**
- Create: `src/DreamBooks.App/ViewModels/SettingsViewModel.cs`, `src/DreamBooks.App/Views/SettingsPanel.xaml(.cs)`
- Modify: `MainMenuButton.xaml` (`SettingsItem` enabled), `MainWindow.xaml(.cs)` (host the panel, `Ctrl+OemComma`), `Strings.resx`, theme tokens, `ThemeResourcesTests`
- Test: `tests/DreamBooks.App.Tests/SettingsViewModelTests.cs`, `SettingsPanelTests.cs`

**Interfaces:**
- Consumes: `SettingsService.Current/Update`, `AppSettings` fields `ThemeMode, AccentOverride, EraserMode, PressureCurve, Smoothing, ToolbarFixed, ToolbarEdge` (existing).
- Produces: `internal sealed class SettingsViewModel(SettingsService settings)` with two-way properties `ThemeMode`, `string? AccentHex` (valid `#RRGGBB` or null for "sistema"), `EraserMode`, `PressureCurve`, `Smoothing`, `bool ToolbarFixed`, `ToolbarEdge`, each calling `settings.Update(...)`, and following external changes (the toolbar changes the same values); `IRelayCommand CloseCommand`. **No finger-drawing option.**

- [ ] **Step 1: Write failing tests**: each property writes through `SettingsService.Update` and shows the new value after an outside `Update`; invalid accent text is rejected and keeps the old value; the panel shows exactly the seven groups, with the pressure curve preview (reuse `PressurePreview`), closes with Esc, the close button and a press outside; the pen presses its controls and a pen press outside closes it and writes (as flyouts); opaque brushes with transparency off.
- [ ] **Step 2: Run** `dotnet test tests/DreamBooks.App.Tests --filter "SettingsViewModelTests|SettingsPanelTests|ThemeResourcesTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** as a centred `FloatingSurface` over the document, 4a language; no second OS window.
- [ ] **Step 4: Run** the same filter plus `MainMenuTests`. Expected: PASS.
- [ ] **Step 5: Commit** `feat: settings panel (FR-110, FR-21, FR-23, UI-02, UI-03)`.

### Task 10: Movable toolbar

**Files:**
- Create: `src/DreamBooks.Core/Settings/ToolbarPlacement.cs`, `src/DreamBooks.App/Views/ToolbarGrip.xaml(.cs)`
- Modify: `src/DreamBooks.App/Views/ToolbarView.xaml(.cs)` (orientation, margins, flyout side, grip), `MainWindow.xaml(.cs)` (top-edge arrangement: search pill and notices below a top toolbar; left edge below the menu button; clear of the rail strip and the UI-04 swipe zone), `Theme.xaml` tokens, `ToolbarViewModel` (edge property fed by `SettingsService`)
- Test: `tests/DreamBooks.Core.Tests/Settings/ToolbarPlacementTests.cs`, `tests/DreamBooks.App.Tests/ToolbarEdgeTests.cs`

**Interfaces:**
- Consumes: `ToolbarEdge` (`Bottom, Top, Left, Right`: check the enum), `SettingsService.Update` (Task 9 uses the same value).
- Produces: `public static class ToolbarPlacement { public static ToolbarEdge NearestEdge(double x, double y, double width, double height); }` (the closest window edge to the grip's release point; ties prefer `Bottom`); `ToolbarView.Edge { get; set; }`, `ToolbarGrip` raising `DragCompleted(Point releasePoint)`; the grip is dragged by finger or mouse only, **never by the pen**.

- [ ] **Step 1: Write failing tests**: `NearestEdge` table (corners, centre → Bottom, a 1 × 1 window), **`NearestEdge` with a 200 × 120 window never throws and returns an edge**; view: on `Left`/`Right` the pill is vertical and flyouts open towards the centre; on `Top` the search pill and notices sit below the toolbar; on `Left` the toolbar is below the menu button and clear of the edge-swipe zone; the rail strip stays free on `Right`; a grip drag by finger ends on the nearest edge and writes `ToolbarEdge` to the settings; a pen press on the grip presses nothing and does not drag; the 4a visibility rules (retract while writing, finger tap) still work on a vertical toolbar; reduced motion → no snap animation; all hit-testing rules of the pill hold on every edge.
- [ ] **Step 2: Run** `dotnet test tests/DreamBooks.Core.Tests --filter ToolbarPlacementTests` and `dotnet test tests/DreamBooks.App.Tests --filter "ToolbarEdgeTests|ToolbarViewTests|NavigationOverlayTests"`. Expected: FAIL.
- [ ] **Step 3: Implement**. Keep the pill sizes and token names of 4a; add only the vertical variants.
- [ ] **Step 4: Run** the same filters. Expected: PASS (existing toolbar tests unchanged).
- [ ] **Step 5: Commit** `feat: toolbar on any edge with a draggable grip (UI-02, FR-110)`.

### Task 11: Export: flattened PDF and page PNG

**Files:**
- Create: `src/DreamBooks.Pdf/FlattenedPdfExporter.cs`, `src/DreamBooks.App/Services/PageImageExporter.cs`, `src/DreamBooks.App/Services/ExportService.cs`
- Modify: `IFileDialogService.cs` / `FileDialogService.cs` (`string? PickSavePng(string suggestedFileName)`), `MainMenuButton.xaml(.cs)` (`ExportItem` enabled with a two-entry submenu), `MainViewModel.cs` (export commands, reuse the progress line of Save), `Strings.resx` (`ExportPdfFlattened` "PDF appiattito…", `ExportPagePng` "Pagina come immagine (PNG)…", suggested-name formats `ExportFlattenedNameFormat` "{0} (appiattito).pdf" and `ExportPageNameFormat` "{0} - pag. {1}.png", failure notice `ErrorExportFailed`)
- Test: `tests/DreamBooks.Pdf.Tests/FlattenedPdfExporterTests.cs`, `tests/DreamBooks.App.Tests/ExportServiceTests.cs`, `PageImageExporterTests.cs`

**Interfaces:**
- Consumes: `PdfDocumentSaver.WriteVerifiedCopyAsync(string sourcePath, string destinationPath, IReadOnlyList<PageChange> changes, CancellationToken)` and the page-change list the Save As pipeline already builds from the session (reuse it, including unsaved strokes); `PdfRenderer.RenderPageAsync`; `InkDrawings` (App) for ink; `FileReplacer`.
- Produces: `public sealed class FlattenedPdfExporter { public Task<string> ExportAsync(string sourcePath, string destinationPath, IReadOnlyList<PageChange> changes, CancellationToken ct); }` (writes the session into a temporary copy, flattens every page's annotations with PDFium's page flatten, verifies, then places the file at the destination; the source file is never opened for writing); `internal sealed class PageImageExporter { public Task<string> ExportAsync(PdfRenderer renderer, int pageIndex, IReadOnlyList<InkStroke> strokes, string destinationPath, CancellationToken ct); }` (200 DPI, white paper, strokes on top, PNG); `ExportService.ExportFlattenedAsync(CancellationToken)` and `ExportPageAsync(CancellationToken)` asking `PickSavePdf/PickSavePng` with the suggested names.

- [ ] **Step 1: Write failing tests**: **round trip** (rules/testing.md): export a PDF with strokes, reopen the result with `PdfRenderer`: no app annotations remain (`StrokeAnnotations.HasAppAnnotation` false on every page), the page text and page count are intact, the rendered page differs from the unannotated render where the stroke was; unsaved session strokes appear in the export; the source file's bytes and timestamp are unchanged; encrypted/restricted source → `PdfSaveRefusedException` and no destination file; cancel/disk-full leaves no temp file and an existing destination is untouched; PNG: pixel size = page size × 200/72, the stroke's pixels are dark, the paper white; `ExportService`: cancelling the save dialog does nothing, an existing target is confirmed by the dialog, a failure shows `ErrorExportFailed` and leaves the document and its dirty state unchanged, reading stays possible while exporting (the command is `IsBusy`-guarded for saving only).
- [ ] **Step 2: Run** `dotnet test tests/DreamBooks.Pdf.Tests --filter FlattenedPdfExporterTests` and `dotnet test tests/DreamBooks.App.Tests --filter "ExportServiceTests|PageImageExporterTests"`. Expected: FAIL.
- [ ] **Step 3: Implement**; PDFium flatten call goes in `Interop/PdfiumNative.cs` next to the existing P/Invokes.
- [ ] **Step 4: Run** the same filters. Expected: PASS.
- [ ] **Step 5: Commit** `feat: export flattened PDF and page PNG (FR-87, FR-88)`.

### Task 12: Requirements, whole-suite sweep, device build and checklist

**Files:**
- Modify: `docs/requirements.md` (FR-03: last 20 files; UI-02: toolbar on any edge by dragging its grip, finger/mouse; UI-03: File recenti, Esporta, Impostazioni, Chiudi documento active; section 10 phase 4b: done pending device test; a "Idee future" note for the pen action button that selects marks; FR-87/FR-88 status), `CLAUDE.md` project structure if new top-level folders appeared, `.claude` rules only if needed

- [ ] **Step 1: Run** `dotnet build` (0 warnings), `dotnet test` (Core, Pdf, App; App three times), `dotnet format --verify-no-changes`, ARM64 build check `dotnet publish src/DreamBooks.App -c Release -r win-arm64 --self-contained -o <temp>` then delete the output.
- [ ] **Step 2: Update** `docs/requirements.md` as listed and tell Fausto what changed.
- [ ] **Step 3: Publish** `dotnet publish src/DreamBooks.App -c Release -r win-x64 --self-contained -o src/DreamBooks.App/bin/Release/net10.0-windows/win-x64/publish-4b` (a new folder; delete older `publish-4a-*`).
- [ ] **Step 4: Write the Italian manual checklist** for Fausto and wait for his results: icon in taskbar/title bar/exe; splash appears fast, closes by itself, with a file passed by "Apri con", and after a crash-restore; start screen B in light and dark, covers appear without flicker, "non trovato" for a moved file, remove, drop a PDF and a non-PDF, tap/pen on a card, "Altri…"; File recenti and Chiudi documento in the menu; settings panel (every group, pen and finger, Esc, theme and accent live); toolbar on the four edges (finger drag, pen does not drag, flyouts, search pill and notices on top, left near the menu, right near the rail), restart remembers the edge; export flattened PDF (open it in another reader, no editable annotations, original untouched) and PNG (page + ink), cancel, existing file, a failure.
- [ ] **Step 5: Commit** `docs: requirements for phase 4b (FR-03, UI-02, UI-03)`; merge to `main` only after Fausto's approval; ask before pushing.
