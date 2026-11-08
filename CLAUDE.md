# Dream Books

> Windows desktop app for reading and annotating PDFs on Microsoft Surface with
> Surface Slim Pen 2: pressure-sensitive ink, eraser on the pen's back end,
> highlighters, bookmarks, undo/redo, and saving to standard PDF. Minimal UI
> with floating, auto-hiding Fluent panels. "Dream Books" is a working name.

Full requirements live in `docs/requirements.md`, with IDs `FR-xx`
(functional), `NFR-xx` (non-functional) and `UI-xx` (interface). Read the
relevant section before starting any feature, and cite requirement IDs in
plans and commit messages.

## Working with the user

- The user is Fausto, a solo developer. Talk to him in Italian.
- Everything in the repository is in English: code, comments, file and folder
  names, commit messages, plans and docs written for Claude. The only
  exception is user-visible UI text, which is Italian and lives in resource
  files (`.resx`), never hard-coded.
- You cannot test pen input, pressure, eraser or visual feel. At the end of
  every task that touches ink, input or visuals, give Fausto a short manual
  test checklist to run on the Surface and wait for his results before
  calling the task done.
- `docs/requirements.md` is a living document. When a decision changes a
  requirement, update it in the same commit and tell Fausto what changed.

## Development process

The Superpowers plugin drives the workflow (brainstorming, written plan, TDD,
implementation, review). Project-specific additions:

- Follow the development phases in section 10 of the requirements. Do not
  start a phase until the previous one has passed Fausto's on-device test.
- TDD is mandatory for `DreamBooks.Core` and `DreamBooks.Pdf`. In
  `DreamBooks.App`, unit-test view models; ink feel and visuals are verified
  with the manual checklist.
- Ask before adding any NuGet package: only MIT, BSD or Apache-2.0 licenses
  are allowed (no AGPL, e.g. iText).

## Project structure

- `src/DreamBooks.App/` — WPF app: views, view models, styles, resources.
  The only project that references WPF. The document view is `Views/`
  DocumentView (virtualised pages, one recycled InkSurface per retained
  page), VirtualPagePanel, PageSurfacePool, ScrollRail (fading rail, page
  preview, go to page) and PageBitmapCoordinator (bitmaps through the render
  scheduler); its state is `ViewModels/DocumentViewportViewModel`. Marks:
  RailTickLayer (ticks on the rail), EdgePanel (left-edge panel shell with
  tabs, IEdgePanelContent), MarksListView with MarkInkThumbnail rows,
  `ViewModels/MarksViewModel`. Search: `ViewModels/SearchViewModel`,
  SearchHighlightLayer (in InkSurface, under the ink). Pages tab:
  PagesListView with PageThumbnail rows, `ViewModels/PagesViewModel`,
  PageThumbnailCache and PageInkCache; thumbnails go through the
  coordinator's low-priority queue (RenderThumbnailAsync, RunWhenIdleAsync).
  Ink: `Ink/` RetainedStrokeLayer (shared base) with CustomInkLayer (pen)
  and HighlighterLayer (under the pen ink); highlighter options in
  `ViewModels/HighlighterOptionsViewModel` (in ToolsViewModel).
  Shell (phase 4a): MainWindow is a WPF UI FluentWindow (Mica, transparent
  title bar; `Views/TitleBarPenInput` lets the pen ink under it).
  `Services/` ThemeService (system/light/dark, accent) and SettingsService
  (debounced save, applied to tools, toolbar, theme). Floating views over the
  document, all on `Views/FloatingSurface` (tint, two shadows, opaque
  fallback): ToolbarView with Pen/Highlighter/EraserOptionsFlyout,
  ColorSwatches and ColorPickerPanel; PageIndicatorChip, NavigationButtons,
  SearchPill, NoticeBar, MainMenuButton, SaveProgressLine. Their state:
  `ViewModels/` ToolbarViewModel, ToolbarVisibilityState (retract while
  writing, finger tap), Pen/EraserOptionsViewModel, CustomPaletteViewModel,
  ColorPickerViewModel.
  Lasso (phase 5a): `Ink/SelectionLayer` (loop, rectangle, handles; in
  InkSurface, whose lasso gestures live in `Ink/InkSurface.Selection.cs`, a
  partial file, incl. the barrel button), `Views/SelectionActionBar` (colours,
  copy, paste, delete, top centre), `ViewModels/SelectionViewModel` and
  SelectionState (a drag only previews; the page changes once, at pen-up).
- `src/DreamBooks.Core/` — domain model, ink math (highlighters: Model
  HighlighterPresets and ChiselTip, Ink HighlighterSettings and
  HighlighterGeometry: constant width, chisel, straight line; lasso: Ink
  SelectionGeometry, SelectionHandles, StrokeTransform, StrokeClipboard and
  the PageEditor Move/Scale/Recolor/Delete/PasteStrokes, all replacements
  through EraseStrokesCommand/AddStrokesCommand), UndoManager,
  DocumentSession (lazy pages, dirty tracking, PageStrokesChanged), stroke
  JSON schema, journal (recorder, replayer), `Settings/` (AppSettings,
  CustomPalette, HexColor, ThemeMode, ToolbarEdge: the pure settings model),
  `Layout/` (DocumentLayout,
  ViewPosition, PositionHistory), `Rendering/` (RenderScheduler: priorities,
  budgeted bitmap cache) and `Marks/` (MarkRecognizer, MarkIndex,
  MarkScanner, MarkTracker, RailTickLayout, MarkTarget: derived data, never
  saved) and `Search/` (TextFolding, SearchIndex, SearchScanner and the
  result types: derived data, never saved). No references to WPF or PDFium.
- `src/DreamBooks.Pdf/` — PDFium (PdfRenderer on its own thread, stroke
  annotations: StrokeAnnotations also writes highlighters transparent, the
  route chosen in HighlighterWriteRoute; PdfDocumentSaver and FileReplacer:
  verified full rewrite, MarkPageSource: the mark scan's uncached stroke
  reads, PdfTextSearcher and
  `Interop/PdfiumText`: text search, one job per page) and
  the app's files under LocalAppData: RecoveryStore (journals,
  `DreamBooks\recovery`) and ViewPositionStore (last position per document,
  `DreamBooks\positions`, plain file I/O) and AppSettingsStore (the settings
  file `DreamBooks\settings.json`, versioned, atomic, never throws).
  References Core. PDFsharp is not
  used.
- `tests/DreamBooks.Core.Tests/`, `tests/DreamBooks.Pdf.Tests/`,
  `tests/DreamBooks.App.Tests/` — xUnit.
- `installer/` — Inno Setup script (`DreamBooks.iss`, Italian texts in its
  `[CustomMessages]`), `welcome.it.txt` and `build.sh`. PowerShell scripts are
  blocked on this machine (execution policy): use bash.
- `docs/` — requirements and plans.

Specific rules live in `.claude/rules/` and load automatically.

## Stack

| Layer | Technology | Notes |
|---|---|---|
| Runtime | .NET 10 (LTS), C# | Targets x64 and ARM64 (NFR-21); dev machine is x64 |
| UI | WPF + WPF UI 4.3 (Fluent styling, MIT) | FluentWindow, Mica, theme manager; panels are a translucent tint (no in-window blur in WPF) |
| MVVM | CommunityToolkit.Mvvm | |
| PDF rendering | PDFium (own P/Invoke over bblanchon.PDFium.Win32) | One dedicated PDFium thread |
| PDF writing | PDFium (same engine as rendering) | Full rewrite to a verified temp file; bookmarks (phase 3) decided later |
| Tests | xUnit | |

## Commands

Run from the repository root.

| Command | Purpose |
|---|---|
| `dotnet restore` | Restore NuGet packages |
| `dotnet build` | Build the whole solution |
| `dotnet test` | Run all tests |
| `dotnet format --verify-no-changes` | Check formatting |
| `dotnet run --project src/DreamBooks.App` | Launch the app (opens a window: only when Fausto asks) |
| `dotnet publish src/DreamBooks.App -c Release -r win-x64 --self-contained` | Release build for on-device testing; Fausto runs `src/DreamBooks.App/bin/Release/net10.0-windows/win-x64/publish/DreamBooks.App.exe` |
| `bash installer/build.sh [x64\|arm64]` | Builds the per-user installer into `artifacts/installers/` (publishes self-contained, compiles `installer/DreamBooks.iss` with Inno Setup 6, checks the result). Version: `<Version>` in the App csproj. Fausto tries `DreamBooks-<version>-x64-setup.exe` |
| `dotnet publish src/DreamBooks.App -c Release -r win-arm64 --self-contained` | Release build for on-device testing: build check only (NFR-21); delete the output afterwards |

Before declaring any task done: `dotnet build` with no new warnings and
`dotnet test` green. Keep this table current: if a command changes, update it.

The dev machine is a Surface with 8 GB RAM and little free disk space: avoid
heavy dependencies and tooling that is not needed.

## General conventions

- Never commit secrets, `.env` files, API keys or tokens.
- When Fausto corrects you twice on the same thing, add a rule here or in
  `.claude/rules/`.

## Notes

Solo project: personal preferences can live in this file. Keep it under ~200
lines; area-specific details go in `.claude/rules/`, workflows in skills.
