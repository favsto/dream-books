# Phase 3b — Finding aids (marks, mark navigation, page thumbnails, text search)

Date: 2026-10-31. Status: **written by Claude on Fausto's delegation ("procedi come è meglio", away from the Surface); not yet read by Fausto.** Every choice marked **[my call]** is mine and can be reversed. Requirement IDs refer to `docs/requirements.md`; the new ones (FR-100..FR-104) are added in the same commit as this document.

## 1. Intent and the why

Fausto reads long books (1,000+ pages) and annotates them. He has a poor memory for where things are. His routine: remember a few words, use text search, refine; when the words are not the real ones he scrolls through all the pages, helped by visual memory and by the marks he left himself. He marks passages today with a **vertical line in the page margin**, an underline or a highlight, and sometimes a note in the margin. What he wants is for the app to *notice* those marks and make them easy to find again, without a new gesture to learn and without complicating the app.

3a gave him continuous scrolling, the scroll rail, go-to-page and back. 3b adds the aids to find things again.

## 2. Goals, non-goals, anti-goals

**Goals**

1. **Marks recognised from ink he already draws.** A *mark* is a margin line (section 4). Highlighted passages become marks when the highlighter exists (3c); the rule is designed now, not wired now. One kind of mark; no levels, no colours, no names to type.
2. **Marks visible on the scroll rail** as small ticks, and **jump to previous/next mark**.
3. **A marks list** (small panel): one row per mark, with a thumbnail of the passage, its page number, and the ink drawn next to it, to jump to it.
4. **Page thumbnails panel** (UI-04, tab "Pagine" only), left edge, auto-hiding, thumbnails *include his ink*, to recognise a page by its look.
5. **Text search** (FR-13, raised from Could to Should): type words, results shown as ticks on the rail (different from marks) and highlighted on the page, next/previous result.

**Non-goals (3b)**

- Page bookmarks (FR-70..FR-76) and writing the PDF outline: **[my call] deferred out of 3b entirely.** Fausto said visual-only marks are enough and not to rely on the PDF's native bookmarks; the marks cover the need, and outline writing is the riskiest piece (PDFium cannot write bookmarks). FR-73 (showing the PDF's existing outline, read-only) is also deferred. The requirement text is updated (see section 8).
- Highlighters (3c). Fuzzy / semantic search, search across several documents, OCR of scanned books (search works only where the PDF has a text layer; the UI says so when there is none).
- Editing, renaming or colouring marks; a "tool" for marking; levels of importance. Two lines on the same passage are **one** mark.
- Two-page view, final styling (phase 4).

**Anti-goals** (the feature fails even if it works)

- Marks that appear where he drew nothing important, so the rail becomes noise; or marks missing for lines he did draw.
- Any stutter or memory growth from scanning 1,000+ pages in the background; the scan must never delay the pen, scrolling or a save.
- Thumbnails that make the panel sluggish, or a memory spike when it opens on a 1,000-page book.
- Search that freezes the UI, loses the user's place, or hides the pen's ink under highlights.
- Changing what is saved in the PDF: **3b writes nothing new into the PDF.** Marks are *derived* from the strokes at runtime; search highlights are a view overlay.

## 3. Constraints

- Core has no WPF/PDFium; PDFium only on the PDFium thread; UI thread only input and drawing (NFR-04).
- The stroke model and its JSON are unchanged (schema v1). No new annotation types. If a mark cache file is added (section 5) it lives outside the PDF, like the position file, is versioned, keyed by the document key and invalidated by the document fingerprint.
- Text from PDFium's text API (`FPDFText_*`, `FPDF_FindText*` family, own P/Invoke over the bblanchon.PDFium.Win32 binary); no new NuGet package.
- Italian text only from `Strings.resx` (Designer in sync); values/durations/colours from `Theme.xaml`; NFR-32 (reduced animation and transparency); 40 px touch targets; AutomationProperties.Name on every command; keyboard reachable.
- Fingers never draw; the pen draws; the new panels and ticks must not take the pen (same rule as the 3a rail: pen never hits them, finger and mouse do).
- Never log document content (including search terms and text); no network.

## 4. Mark recognition (FR-100, FR-104)

**[my call] Rule, tunable in one constants class:** a stroke is a *margin line* when
- its drawn path is **nearly vertical**: the straight line from first to last point is within 20° of vertical, and the path's length is at most 1.3× that straight distance (so a slightly wavy or tilted line counts, a zig-zag or a scribble does not);
- its vertical extent is at least **3 text lines** of the page. Line height is estimated from the page's text layer when present (median text line height), else a fixed 36 pt;
- it lies in the **outer margin zone**: its horizontal position is within the outer 18% of the page width on either side, or outside the text block's horizontal bounds when the text layer gives them;
- it is drawn with a pen tool (not eraser); the stroke's colour and width do not matter.

Several qualifying strokes that overlap or touch vertically (gap ≤ 1.5 lines) on the same side form **one mark**, whose extent is the union (FR-104: "two or more lines on the same passage = one mark"). A mark has: page index, vertical extent (page points), side, and the ids of its strokes. **Marks are derived data**: recomputed from the strokes; nothing is stored in the PDF or the journal. Erasing or undoing the line removes the mark; drawing one adds it, live, while he works (the rail tick appears after the stroke is committed; no recognition runs during the stroke).

The **margin note** attached to a mark = the strokes (non-mark) whose bounding box lies within the mark's vertical extent ±1 line and in the same margin zone or within 25% of the page width from it. They are only *shown* in the marks list (as a small drawn thumbnail of the margin region); no handwriting recognition.

The highlighter rule (FR-100 second half): when 3c lands, each highlighter stroke group that covers at least 3 words or 1 line of text becomes a mark with the same behaviour. 3b only defines the `IMarkSource` seam so 3c adds a source without touching the consumers.

The recogniser is pure Core code, TDD'd on stroke fixtures (straight, wavy, tilted, short, zig-zag, text-block-edge, rotated pages, pages with no text layer).

## 5. The mark index (document-wide, background)

Rail ticks and "next mark" need marks for **all pages**, but strokes load lazily per page. A background **mark scan** loads each page's strokes through the existing stroke loader at idle priority (below render requests, never during a stroke, paused during a save), classifies them, and keeps `MarkIndex` in memory: per page a sorted list of marks. The scan starts after open, processes pages nearest the current position first, then outward, and reports progress (ticks appear as the scan advances; no spinner needed).

**[my call]** First version: in-memory only. If the scan of Fausto's 1,076-page book takes more than ~5 s of wall clock (measured in the integration test and on the device), add the persisted index file (`%LOCALAPPDATA%\DreamBooks\marks\<DocumentKey>.json`, versioned, invalidated by `DocumentFingerprint`, pruned like positions). Pages known to have **no annotations** are skipped cheaply: the scan must not parse JSON for pages without our annotations (builder: find the cheapest PDFium check, e.g. the page's annotation count).

The index is updated incrementally from `DocumentSession` (strokes added/erased/undone/redone on a loaded page) so edits are reflected at once.

## 6. UI

- **Rail ticks (UI-07, finished here):** small ticks on the existing rail at the mark's page position, one colour for marks (theme token) and a different one for search results; ticks closer than one pixel merge. The rail already fades in and out; ticks are drawn only when it is visible. Tapping near a tick while dragging snaps the knob to the nearest mark within 8 px (**[my call]**, optional, can be cut if awkward).
- **Previous/next mark:** two buttons in the navigation panel (with Back and go-to-page) and keyboard shortcuts (**[my call]** `Alt+Up` / `Alt+Down`, plus `F2`/`Shift+F2` for search results). They go to the mark's vertical extent, centred if it fits the window, and **record Back history** like go-to-page.
- **Marks list:** a panel opened from the menu/shortcut, left edge (shares the panel of UI-04): rows of thumbnail of the marked region at the mark's own page position with its margin note, "pag. N". Tap = go there (history recorded). Shows a count; empty state text tells him what counts as a mark ("linea verticale sul fianco, lunga almeno 3 righe").
- **Pages panel (UI-04, tab Pagine):** a virtualised vertical list of page thumbnails (≈140 px wide) **including his ink** (the ink is drawn as vectors over the low-res page bitmap so it is exact and cheap), current page ringed, pages with marks marked with the mark tick colour. Opens with a finger swipe from the left edge or the menu, closes with swipe or tap outside, auto-hides; pen never opens/hits it. Thumbnail renders go through the existing `RenderScheduler` at a lowest priority and a separate small cache (budget 40 MB, **[my call]**). Tap = go to the page (history recorded).
- **Search:** a search field in the navigation panel (`Ctrl+F` focuses it); search runs in the background page by page starting from the current page and wrapping, results counted live ("3 di 17"); the current result is highlighted on the page with a translucent overlay (view-only, theme colour, never covers ink: drawn *below* the ink layer), all results as ticks on the rail; next/previous with Enter/Shift+Enter and buttons; Esc clears. Case-insensitive, accent-insensitive **[my call]** (Italian text: "e" matches "è"); whole-document, no regex. A document without a text layer: the field says so once ("Nessun testo ricercabile in questo PDF").

## 7. What is left to the builder

The exact classification constants after trying them on generated fixtures and a copy of the real book (only counts and times reported, never text); the cheapest annotation pre-check; the thumbnail ink drawing (reuse of `CustomInkLayer` / stroke contours at thumbnail scale); the search tokenisation (PDFium `FPDFText_FindStart` with the flags it supports plus a normalisation pass for accents if the flags do not cover it); the panel shell for the left-edge panel (shared by Pages and Marks list); splitting into the two stages below.

**Stages (two plans, tested one after the other):**
- **3b-1 — Marks:** recogniser, mark index + background scan + live updates, rail ticks, previous/next mark, marks list. (Pure Core + view-model heavy; small UI.)
- **3b-2 — Thumbnails and search:** left-edge panel with the Pages tab, thumbnail pipeline with ink, text search with highlights and rail ticks.

## 8. Requirement changes (made in the same commit)

- Phase 3 line in section 10: 3b redefined as above; page bookmarks and outline writing moved to a later block ("3d", not scheduled), because Fausto asked for visual-only marks.
- FR-70..FR-76: marked **deferred (not in 3b)**; FR-74 (outline write) stays Must for the final v1 but is not scheduled; flagged as an open decision.
- New FR-100 (margin line = mark), FR-101 (marks as ticks on the rail), FR-102 (previous/next mark), FR-103 (marks list), FR-104 (overlapping lines merge into one mark).
- FR-13 (search) raised from Could to Should; new FR-105 (search results as ticks and highlighted on the page, next/previous).
- UI-07: the rail marks are *marks and search results* (no bookmarks, no separate "evidenziazioni" colour: highlights become marks).
- UI-04: only the "Pagine" tab in 3b; the "Segnalibri" tab appears when bookmarks are built.

## 9. Testing

- Core TDD: mark recogniser (the fixture matrix above), merge rule, `MarkIndex` (incremental add/erase/undo, ordering, neighbour lookup for next/previous, wrap-around), scan scheduler logic (priority order, pause, cancel) with fakes.
- Pdf: text search and text-layer metrics round-trips on generated PDFs (with and without a text layer, accents, ligatures, rotated pages); annotation pre-check.
- App: view-model tests (marks list, next/previous with history, search state machine), thumbnail pipeline with fakes, rail tick layout math; STA smoke tests (panel open/close, no pen interception, virtualisation of the thumbnail list).
- Integration on the real book copy (skip when absent): scan time, memory, ticks count.
- Manual checklist for Fausto (Italian), written at the end of each stage.
