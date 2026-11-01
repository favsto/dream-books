# Phase 3a — Multi-page document viewer: design

Date: 2026-10-31 · Status: approved by Fausto (2026-10-31)
Requirements: `docs/requirements.md` section 5 and section 10 phase 3 (first of three blocks); FR-05, FR-06, FR-07, FR-09, FR-10, FR-04, FR-26, FR-27, NFR-02, NFR-03, NFR-04, NFR-05, NFR-06, NFR-07, NFR-22. Blocks 3b (navigation aids: thumbnails with ink, rail marks, jump between notes, bookmarks, text search) and 3c (highlighters) build on this one and are designed later.

## 1. Intent

Until now the app shows one page. Fausto reads long books and work documents and moves through them by scrolling, bookmarks, a page-preview panel, his own notes and text search. This block gives the app the foundation for all of that: the whole document as one continuous, smooth vertical scroll, with zoom, jump-to-page, a position that is remembered, and a discreet scroll rail that appears while scrolling and fades away by itself. Pen and finger behaviour (pen writes, finger scrolls, back end erases) is already what he wants and must stay exactly as is.

What he told us he likes and dislikes (his words, played back and confirmed): scrolling that is linear and soft with no visible stutter or lag; a small overlay that appears while scrolling to show where he is and disappears on its own with soft effects; minimal interfaces without controls that are not needed at that moment; one page at a time at full width. He chose design **B** for the overlay: a thin rail on a side edge; touching or dragging it shows one large preview of the page; the rail later carries coloured marks (block 3b).

## 2. Goals

- **Continuous vertical scroll of the whole document**, pages centred on a neutral grey background with a small gap, default zoom "fit width" (our guess, to confirm in the manual test).
- **Smooth scrolling and zooming**: 60 fps target (NFR-03); only the pages near the viewport are drawn (the ones in view plus 2 before and 2 after, NFR-05); a page that is not rendered yet shows a placeholder (a white page of the right size with a light fade-in), never a blank screen; ink is vector, drawn from the model and always sharp; the PDF bitmap of a page is re-rendered at the right resolution once zoom settles (as today, NFR-07).
- **Zoom 25%–800%**: pinch with two fingers, Ctrl + wheel, Ctrl + / Ctrl −; double tap with one finger toggles "fit width" and 200% around the tapped point (FR-06, FR-07). Zoom keeps the reading position: the point under the fingers (or the centre of the window) stays where it is. When the page is wider than the window it can be panned horizontally with the finger.
- **Jump to page**: tapping the "12 / 48 · 125%" indicator opens a numeric field (FR-09); PgUp, PgDn, Home, End (FR-10).
- **Back button**: after any jump (go to page, dragging the rail) a "back" action returns to the previous position, like a browser (stack of positions).
- **Remember the position**: reopening a document returns to the last page, zoom and offset (FR-04). Documents with unsaved changes already restore through the journal; this adds saved documents.
- **The rail (design B)**: while the user scrolls, a thin rail appears on the right edge with a knob showing the position; touching/dragging the rail scrubs through the document and shows one large preview of the page under the knob (a light, low-resolution render); both fade away after about 1.5 s of stillness, with a soft ease-out. Fades respect the Windows animation setting (off = instant) (NFR-32). In this block the rail has no coloured marks yet (3b).
- **Strokes on every page**: the pen writes on whichever page is under it, several pages can be visible at once, the eraser (back end or eraser tool) works on the page it starts on, undo/redo already spans pages and navigates to the page of the command (existing behaviour must keep working with several visible pages).
- **Large documents**: opening a 1,000-page book keeps the first page visible within 1 s (NFR-02); memory stays under 600 MB with a 300-page document and 2,000 strokes (NFR-06).

## 3. Non-goals

Two pages side by side (FR-08), page rotation of the view (FR-12), thumbnails panel, coloured marks on the rail, jump between notes, bookmarks, text search (block 3b), highlighters (block 3c), the final floating UI, themes and settings (phase 4), any change to how strokes are saved or journalled except what the view state needs (section 5).

## 4. Anti-goals — failure even if the tests pass

- Any visible stutter, lag or frame drop while scrolling or pinching, on a page with many strokes (Fausto already saw lag with ~1,500 strokes: the per-stroke retained visuals fixed it and must not be lost with several pages visible).
- A white or blank page flashing where a page should be; pages popping in under the finger; the page jumping under the user's fingers when zoom changes or when pages above are rendered or resized.
- Ink out of register with the page bitmap at any zoom or after any re-render; ink drawn on the wrong page; a stroke crossing a page edge not clipped at the edge (FR-27).
- Pen scrolling or finger writing (regressions of what phase 1 solved); the zoom lock during a stroke being lost.
- Reading position lost after a restart; position restored to a different place; the back button returning somewhere unexpected.
- Memory or CPU growth while scrolling through a long book (bitmaps not released, visuals not recycled).
- Controls permanently visible that were not requested; overlays that do not disappear.

## 5. Decisions

### Made with Fausto
- Blocks 3a, 3b, 3c in this order; this document covers 3a only.
- One page at a time, full width; no two-page view.
- Overlay design B (thin rail + large preview, fading); marks come in 3b.
- Navigation aids Fausto wants overall (for 3b): thumbnails that show his ink, marks on the scroll rail, jump between notes, bookmarks, search, back button (the back button is in this block), resume at last position (this block).

### Claude's calls (Fausto can redirect)
- **Layout math lives in Core, pure and tested (TDD):** a `DocumentLayout` (page sizes in points, gap, zoom → page tops/heights in view units, visible range for a viewport, mapping between a scroll offset and a position expressed as page index + offset inside the page in page points) and a viewport/zoom state with anchored zoom. The position is always stored as (page index, offset within the page in page points, zoom), never as raw pixels, so it survives window resizes, DPI changes and zoom.
- **View composition in App:** a virtualised host (a custom `Panel` or a `ScrollViewer` with a custom items layout, builder's choice) that creates a page surface (the existing `InkSurface` with its bitmap layer, retained ink layer, wet renderer and eraser cursor) only for pages in the retained window and recycles them as the window moves; off-window pages cost nothing but their placeholder geometry. The existing single-page `PageView` logic (zoom lock while inking, debounced re-render, manipulation/Ctrl+wheel) is generalised, not rewritten from scratch.
- **Render scheduling:** PDFium has one thread. Page renders go through a small scheduler: priority to pages in view, then neighbours; renders of pages that left the window are cancelled; a low-resolution render first (so a fast scroll shows something quickly), then the sharp one; results are cached in an LRU of bitmaps with a byte budget (default 250 MB, so that bitmaps + ink + model stay under the 600 MB goal); the budget and window size are constants in one place. A save in progress occupies the PDFium thread for seconds (documented): renders wait behind it and placeholders stay visible; the rail and scrolling must stay responsive meanwhile.
- **Page visibility and "current page"**: the current page is the one covering the centre of the viewport; `DocumentSession.CurrentPageIndex` follows it (debounced) so undo navigation, the indicator and the journal view-state use one notion. Pages become "loaded" in the session (strokes read) when they enter the window, not when they are merely scrolled past quickly (builder: debounce).
- **Fit width** is a mode: while on, resizing the window recomputes zoom; any manual zoom turns it off; double tap toggles it.
- **Remembering the position for saved documents:** stored outside the PDF, in a small per-document file under `%LOCALAPPDATA%\DreamBooks\positions\<DocumentKey>.json` (same key as the recovery journal), written when the view settles (500 ms, as the journal view-state) and when the document closes; the PDF is never touched for this. If a journal exists its view-state wins. Entries older than 365 days or beyond 500 documents are pruned.
- **Journal view-state compatibility:** the existing `view` entry (page, zoom, scrollX, scrollY) is kept; scroll offsets are redefined as offsets inside the page in page points. No new journal format version unless the builder needs it (then old journals must still restore).
- **The rail:** drawn by the app (WPF), 40 px touch target on the right edge, appears on scroll/hover/touch near the edge, fades after 1.5 s idle (180 ms in, 200 ms out, ease-out; instant when Windows animations are off); the large preview is a ~300 px-wide low-resolution render from the scheduler (no ink in 3a; ink in previews comes with the thumbnails in 3b); on high-density screens the preview is rendered at the screen's scale. **Pen and the rail (changed 2026-11-01 after the on-device test):** the pen only grabs the knob (its bounds plus about 8 DIP) while the rail is shown, and drags it like a finger; elsewhere on the rail (strip, ticks, indicator) it writes, and with the rail hidden it writes everywhere. Fingers and the mouse grab the knob from a wider invisible zone (at least 56 × 56 DIP, reaching into the page).
- **Back button**: a position stack (max 20) pushed on go-to-page and when a rail drag starts; shown as a small button in the existing spartan panel (the real placement is phase 4); Alt+Left also triggers it.
- **Pen/touch routing is unchanged and must keep working per page surface**: pen → ink on the page under it; finger → scroll/pan/zoom; back end → eraser; mouse as before.
- **Every visible page is inkable at once (answer to the builder check).** Each page surface gets its own `PageEditor` (the session exposes an editor for any loaded page, not only the "current" one; `MainViewModel.Editor` becomes the editor of the current page for the panel/undo only). Pen-down on a visible page does NOT change the current page: "current page" only follows the viewport centre, so the indicator, the journal view-state and undo navigation never jump while the user writes. An eraser gesture belongs to the page where it starts (one gesture at a time; the "active erase" is per page). Undo/redo scrolls only when the command's page is not visible at all; if it is visible, the view stays where it is.
- **Zoom with pages of different sizes (answer to the builder check).** Zoom is one number for the whole document; 100% means 1 point = 1/72 inch drawn at 96/72 DIP (as today). "Fit width" fits the widest page of the document, so the zoom never changes while scrolling past a different-sized page; narrower pages are centred. The stored position (page, offset inside the page, zoom) restores correctly on a window of a different size; in fit-width mode the zoom is recomputed.
- **Open time of a big book (answer to the builder check).** Page sizes are read eagerly at open (measured on Fausto's 1,076-page book: 0.5–0.65 s), so the layout is exact from the first frame and nothing jumps; there is no "sizes still arriving" state. The builder may optimise `PdfRenderer.Open` if profiling on a larger book shows a problem, but must keep the layout exact from the start.
- **Scrolling during a save (answer to the builder check).** Scrolling, zoom, go to page, the rail and the back button stay enabled during a save (the earlier navigation lock goes away). Pages entering the window while the PDFium thread is busy show the placeholder; their strokes and bitmaps load when the save ends (seconds), and until a page's strokes are loaded that page does not accept ink (pages already loaded keep accepting it). During the brief release-and-reopen window of a plain Save, page renders that fail are retried automatically once the new renderer is in place.
- **Debug panel additions** (spartan, Italian from resx): a "go to page" field, back button, zoom controls already there (fit width stays); no new permanent controls beyond these.

## 6. Constraints

- Core stays free of WPF/PDFium; PDFium calls only on the PDFium thread; the UI thread only handles input and drawing (NFR-04); strokes in page points, conversion only in the view layer (FR-26).
- Nothing in this block may change how strokes are saved, verified or journalled beyond the view-state meaning above; the save pipeline, journal and recovery must keep working with several pages visible (their tests must stay green).
- User-visible text only in resx (Italian) with the Designer in sync (drift test). Nullable on; file-scoped namespaces; one public type per file; warnings are errors; TDD in Core (layout math, scheduler logic, position store); view models unit-tested; STA smoke tests for composition; the feel (smoothness, fades) is verified by Fausto on the Surface.
- Dev machine: 8 GB RAM, little disk: no new heavy packages; no new NuGet packages are expected; ask before adding any.

## 7. Testing

- **Core (TDD):** layout math (tops/heights at several zooms, visible range with margins, offset↔position round trips, anchored zoom keeps the anchor point within ±0.5 px, window resize in fit mode, extreme sizes, 10,000 pages, pages with different sizes and rotations as displayed), position stack (push/pop/limits), position store (round trip, pruning, corrupt file, missing folder, concurrent writers), render-scheduler logic as a pure class (priority order, cancellation of left pages, LRU byte budget, low-res then sharp ordering) with fake clocks/fake renderer.
- **App:** view-model tests (current page follows the centre, go-to-page, back stack, last-position restore flow, journal view wins over saved position), STA smoke tests (a window with a 30-page generated PDF: pages virtualised — only the window's pages are instantiated; scrolling programmatically recycles surfaces; ink on a page not at the top; erase and undo across visible pages), memory/recycle tests (surfaces created count bounded after scrolling 300 pages), no UI-thread PDFium calls.
- **Manual checklist (Fausto, Surface):** scroll fluidity on a long book and on a page with 1,500 strokes; no white pages; pinch/double-tap/Ctrl+wheel zoom keeps the position; rail appears, fades, drag preview; go to page; back button; close and reopen returns to the same place; pen/finger/back-end unchanged; memory in Task Manager after scrolling the whole 1,000-page book.

## 8. Left to the builder

(The four questions raised by the builder check are answered in section 5: per-page editors, document-wide zoom with fit-to-widest-page, eager page sizes at open, behaviour during a save.) The exact virtualisation mechanism and class names, constants (window size, LRU budget, preview size, fade timings within the stated values), how the low-resolution first render is produced, the debounce of "current page" and of page loading, the file format of the position store, Italian wording, test fixture contents, and the task breakdown.

## 9. Requirement changes to make in the same commit as this design

- Section 10 phase 3 line: split into 3a/3b/3c with the contents above (documents the agreed order).
- FR-04: clarify it is stored outside the PDF (local per-document file) and applies to documents without unsaved changes too; FR-09/FR-10 unchanged.
- New requirement UI-07 (Should): the scroll rail with page preview that appears while scrolling and fades away (design B); marks on it are described in 3b.
- NFR-06/NFR-05 unchanged (the 250 MB bitmap budget is an implementation choice under them).

## 10. Done means

Build with no new warnings, all tests green, Fausto's manual checklist run on the Surface, the requirement changes above committed with the decision, and his approval to merge into `main`.
