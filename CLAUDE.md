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
  scheduler); its state is `ViewModels/DocumentViewportViewModel`.
- `src/DreamBooks.Core/` — domain model, ink math, UndoManager,
  DocumentSession (lazy pages, dirty tracking), stroke JSON schema, journal
  (recorder, replayer), `Layout/` (DocumentLayout, ViewPosition,
  PositionHistory) and `Rendering/` (RenderScheduler: priorities, budgeted
  bitmap cache). No references to WPF or PDFium.
- `src/DreamBooks.Pdf/` — PDFium (PdfRenderer on its own thread, stroke
  annotations, PdfDocumentSaver and FileReplacer: verified full rewrite) and
  the app's files under LocalAppData: RecoveryStore (journals,
  `DreamBooks\recovery`) and ViewPositionStore (last position per document,
  `DreamBooks\positions`, plain file I/O). References Core. PDFsharp is not
  used.
- `tests/DreamBooks.Core.Tests/`, `tests/DreamBooks.Pdf.Tests/`,
  `tests/DreamBooks.App.Tests/` — xUnit.
- `docs/` — requirements and plans.

Specific rules live in `.claude/rules/` and load automatically.

## Stack

| Layer | Technology | Notes |
|---|---|---|
| Runtime | .NET 10 (LTS), C# | Targets x64 and ARM64 (NFR-21); dev machine is x64 |
| UI | WPF + WPF UI (Fluent styling) | Windows 11, Mica and Acrylic |
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
