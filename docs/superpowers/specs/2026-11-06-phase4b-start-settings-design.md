# Phase 4b design: start experience, settings window, export, movable toolbar

Written for the builder of the plan. Decisions marked **(Fausto)** were stated by Fausto in the brainstorming of
2026-11-06; **(call)** marks a decision made by Claude that Fausto can still change. Requirement IDs refer to
`docs/requirements.md`; the phase-4b line of section 10 and UI-02, UI-03 are updated in the same commit as the code.
Mockups of the splash and the start screen: `.superpowers/brainstorm/4b-start-mockups.html` (rough, not final design).

## Intent

Phase 4a made the reading window. 4b makes the app feel like a finished product the first minute: it has its own
face (icon, splash), it opens on something useful when no PDF is open (recent documents, one button to open a PDF),
and the remaining menu entries that were disabled in 4a work (recent files, export, settings). The toolbar can also
live on the other three edges.

## Goals

1. **App icon (Fausto).** Use `extra/dream-book-icon.png` (green round badge: open book and yellow pencil) as the
   application icon (exe, taskbar, window). The source has a white background outside the circle and is only
   245 × 245 px: make the outside transparent and build a multi-size `.ico` (16, 24, 32, 48, 64, 128, 256). The
   upscaled 256 is soft; Fausto may later provide a larger source and the build must make replacing it a one-file
   change. **(call)** The processing is done once and the results (transparent PNG, ico) are committed; no build-time
   image tooling and no new NuGet package.
2. **Splash (Fausto: "ok lo splash, con la mia icona").** Shown from process start until the main window is ready to
   use, then it closes by itself. Content: the icon, "Dream Books", a short Italian tagline, a progress indicator.
   **(call)** Tagline "Leggi. Annota. Sogna."; the indicator is indeterminate; no artificial delay (it never stays
   longer than the real start-up); with Windows animations off the indicator is static (NFR-32). The splash must
   appear fast: it is created before the heavy start-up work. It follows the theme only if that is free; a fixed
   dark look is acceptable **(call)**. All user-visible text in `.resx`.
3. **Start screen (Fausto: variant B, "copertine").** With no document open, the document area shows: a title row
   "Riprendi a leggere" with a primary button **Apri PDF…** (the same command as Ctrl+O), and below it the recent
   documents as cover cards: first-page image, file name, when it was last opened ("ieri", "3 giorni fa"), and a thin
   reading-progress bar (page reached / page count). Tap/click a card opens the document at its saved position
   (FR-04). A last card "Altri…" appears when more recents exist than fit **(call)**, opening a simple list.
   Cards are 40 px-class touch targets or larger; pen and finger both press them (same rule as the other floating
   controls); the pen never draws on the start screen.
   - **(Fausto, "ok le tue ipotesi")** Recents remember where the reader stopped. A recent whose file is missing is
     shown as "non trovato" and can be removed from the list. Dropping a PDF on the window opens it. The start screen
     comes back when the document is closed (there is no document-close command yet: if that needs adding it is in
     scope, **(call)** as "Chiudi documento" in the menu).
   - **(call)** The list holds at most 20 entries; the start screen shows as many cards as fit, newest first.
   - **(call)** The cover image is the page-1 low-resolution bitmap rendered through the existing thumbnail path
     and cached in a small local folder under `%LOCALAPPDATA%\DreamBooks`, bounded in size; it is rendered off the
     UI thread and never delays opening a document.
4. **Recent files (menu entry "File recenti", UI-03).** Same data as the start screen, as a submenu/list in the menu
   panel; enabling this entry is in scope.
5. **Settings window (Fausto: contents "per ora ok").** Opened from the menu "Impostazioni" (and Ctrl+,
   **(call)**). It edits exactly what FR-110 already stores and 4a only exposed through the toolbar: theme (sistema,
   chiaro, scuro), accent colour override, eraser mode, pressure curve with preview (FR-21), smoothing (FR-23),
   "Toolbar fissa", toolbar edge. Changes apply
   immediately and are saved through the existing `SettingsService` (debounced, atomic). **(call)** The settings are
   a floating panel centred over the document in the 4a surface language (not a second OS window), so the pen/finger
   rules and the theme/transparency fallback are the existing ones; it closes with Esc, a close button, or a press
   outside it. **(Fausto)** "Disegna anche con il dito" is removed for good: fingers never draw, there is no such
   setting, and it is removed from the requirements.
6. **Export (Fausto: both).** Menu "Esporta" with two entries, both asking where to save through the standard save
   dialog:
   - **PDF appiattito (FR-87):** a new PDF in which strokes, highlighters and margin notes are merged into the page
     content and no longer editable. The open document and the original file are never touched (NFR-10: write to a
     temporary file, verify, then place it). Round-trip test required (rules/testing.md): reopen the exported PDF and
     compare page content.
   - **Pagina come PNG (FR-88):** the current page with its ink on top. **(call)** 200 DPI, white paper, no
     transparency.
   - **(call)** Suggested names are "<name> (appiattito).pdf" and "<name> - pag. N.png"; an existing target asks
     before replacing. Progress uses the existing thin save-progress line; reading stays possible meanwhile; a
     failure shows a notice and leaves everything as it was.
7. **Movable toolbar (UI-02, `ToolbarEdge`).** The toolbar can sit on any of the four edges and the choice is
   remembered (the setting already exists). **(call)** How: the toolbar has a small grip at one end; a finger or the
   mouse drags it and on release it snaps to the nearest edge with a short animation (instant with reduced motion);
   the pen does not drag it (the pen presses its controls as in 4a). The same choice is in the settings window. On
   the left and right edges the pill is vertical; the option flyouts open towards the centre of the window. The
   left-edge panel (UI-04) and the scroll rail (UI-07) keep their own positions: the toolbar on the left or right
   sits clear of them (offset so it never hides the rail's strip or the panel's close button). The automatic
   visibility rules (retract while writing, finger tap) are unchanged on every edge.

## Non-goals

- Anything about bookmarks, the lasso/selection tool, OCR, text editing.
- **Pen action button ("seleziona i segni") (Fausto):** a future idea, not a setting and not in 4b. Do not design
  for it beyond not blocking it; it is recorded in the requirements as a future idea.
- A first-run tutorial, accounts, sync, telemetry. The app makes no network calls (NFR-40).
- Other languages: texts are Italian in `.resx`, ready for localisation.

## Anti-goals (what would make 4b a failure even if it technically works)

- The splash makes start-up slower or hides a hang; or flashes a white window.
- A recent entry or cover leaks document content or paths into the settings file or the logs (NFR-12;
  FR-110 says the settings file never contains document paths). The recents list is a separate file.
- The pen ink latency or any 4a pen rule regresses (NFR-04), or the pen starts dragging the toolbar.
- The start screen shows white boxes or flicker while covers load; a missing file crashes it.
- Export touches the original PDF.

## Constraints

- All of `CLAUDE.md` and `.claude/rules/`: Core never references WPF/PDFium; every model change through
  `UndoManager` (export and recents are not model changes); tokens only in theme dictionaries; light and dark;
  NFR-32 fallbacks (opaque brushes, no animation); 40 px targets; names for automation (NFR-31); Italian `.resx`.
- No new NuGet package without asking; only MIT/BSD/Apache-2.0.
- Recents live in `%LOCALAPPDATA%\DreamBooks` as a versioned file written atomically, never throwing, like the
  other stores; the persistence code is in `DreamBooks.Pdf` (plain file I/O, as `ViewPositionStore`) or Core
  for the pure model, with TDD there.
- The dev machine has 8 GB RAM and little disk: keep the cover cache small and bounded.
- Pen feel and visuals cannot be tested automatically: finish with a numbered Italian manual checklist for
  Fausto and wait for his device test before merging (phase rule).

## Left to the builder

Splash technique (native splash image vs a lightweight WPF window), the exact animation of the icon/indicator, the
recents file schema, how covers are cached, the layout details and spacing of cards and of the settings panel,
the vertical layout of the toolbar, task order and decomposition, and the tests.

## Calls added after the builder check (Claude's, changeable by Fausto)

- **Export content:** both exports contain the session as it is on screen, including strokes not yet saved; the
  pipeline works from the session's strokes written into a temporary copy, never from the file on disk. Annotations
  of other programs that the app shows read-only are flattened too. "Margin notes" are ordinary strokes (no separate
  object).
- **Cold start order:** the splash stays until the window is ready and, when the last journalled session is
  restored (FR-86) or a file is passed on the command line ("Apri con"), until that document has opened; then the
  document shows and the start screen is skipped. A file argument wins over the restore. With neither, the start
  screen shows. The splash never waits for covers. NFR-01 (cold start to start screen under 1.5 s) is the target
  of splash plus start screen.
- **Toolbar on the top edge:** it sits below the title bar; the search pill and the notices move below it while it
  is there. On the left edge it sits clear of the menu button (below it) and of the UI-04 swipe zone.
- **Recents count:** 20, so FR-03 ("ultimi 8 file") is updated to 20 in the same commit.
- **Covers:** the page-1 bitmap only, without ink (it never goes stale after a save). A recent from before 4b has a
  placeholder card until the file is next opened; covers are never rendered in the background for files that were
  not opened.
