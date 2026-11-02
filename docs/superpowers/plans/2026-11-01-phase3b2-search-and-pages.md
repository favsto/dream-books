# Phase 3b-2 Text Search and Page Thumbnails Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Text search of the whole PDF (results highlighted on the page, ticks on the scroll rail, next/previous) and a "Pagine" tab in the left-edge panel showing page thumbnails that include Fausto's ink, without writing anything into the PDF.

**Architecture:** Pdf gets the PDFium text API (own P/Invoke) behind a `PdfTextSearcher` that returns normalised matches with rectangles in displayed page points. Core gets pure, tested code: `TextFolding` (case/accent-insensitive normalisation with index map), `SearchIndex` (results, current result, wrap-around navigation) and `SearchScanner` (background page-by-page search from the current page, pause/cancel rules like the 3b-1 mark scanner). App adds a search UI in the navigation panel, a highlight layer under the ink in each page surface, a third tick kind on the rail, and a second tab in the `EdgePanel` with a virtualised thumbnail list fed by the existing page-bitmap pipeline plus vector ink.

**Tech Stack:** unchanged (.NET 10, WPF, PDFium via bblanchon.PDFium.Win32, CommunityToolkit.Mvvm, xUnit). No new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-10-31-phase3b-finding-aids-design.md` (sections 3, 6 (Pages panel, Search), 7, 9, 10 are binding for this stage). Builds on 3a and 3b-1 (both implemented; 3a/3b-1 on-device tests still partly pending: do not regress them).

## Global Constraints

- Core never references WPF/PDFium; PDFium only on the PDFium thread (NFR-04); the UI thread only handles input and drawing; **nothing new is written into the PDF or the journal** (search highlights and thumbnails are view-only); stroke model and JSON schema v1 unchanged.
- The search and the thumbnail work run at low priority: never delay the pen, scrolling, a render or a save; pause during a stroke and during a save; cancel on a new query, on document change and on close; no unobserved task exceptions; memory bounded (thumbnail cache budget 40 MB; no per-page text kept after its page was searched, only match rectangles).
- Never log document content: search terms, matched text and page text are never logged or put in exception messages, journal or test output (tests print counts and times only). No network.
- Italian text only in `Strings.resx` (+ hand-written `Strings.Designer.cs`, drift test); values/colours/durations in `Theme.xaml`; NFR-32 (reduced animation and transparency honoured); 40 px touch targets; `AutomationProperties.Name` on every command; keyboard reachable.
- **Pen rule:** the pen draws. New controls (search field, buttons, tab strip, thumbnail list, highlight layer) never intercept the pen outside their own visible bounds while open; the highlight layer is not hit-testable; the Pages tab follows the same finger/mouse/pen rules as the 3b-1 panel (closed: the edge inks; open: a pen press on the sheet closes it).
- Shortcuts: `Ctrl+F` focuses the search field; `Enter` / `Shift+Enter` next/previous result while the field has focus; `F3` / `Shift+F3` next/previous result anywhere; `Esc` clears the search and returns focus to the document. Check for clashes with the existing bindings (Alt+Left, Alt+Up/Down, Ctrl+M, Ctrl +/-, PgUp/PgDn/Home/End).
- Nullable on; file-scoped namespaces; one public type per file named after the type; `_` prefix for private static readonly/const; warnings are errors; TDD in Core/Pdf; view models unit-tested; never run the WPF app; do not stage `.claude/settings.json`; commit trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`; `dotnet build` 0 warnings, `dotnet test` green, `dotnet format --verify-no-changes` clean at the end of every task.
- Real-book tests: copies only in `C:\Users\Favsto\workspace\temp` (or `DREAMBOOKS_FIXTURES`), skip when absent, report only counts and times.

## Rulings made while planning (reversible)

- Search is case-insensitive and accent-insensitive (Italian: "e" matches "è"), whole document, no regex, plain substring match of the normalised query (one or more words; internal whitespace collapsed; no whole-word option).
- Navigation **wraps** around the document for search (last → first, with a short message), unlike marks.
- The recogniser keeps its fixed 15 pt line height: **the text-layer line height is NOT swapped into the mark rules in this stage** (the marks work well on Fausto's pages; revisit later). Only search uses the text layer.
- "Next/previous note" and a Notes tab in the marks list are **not** part of this stage (awaiting Fausto's decision).
- The Pages tab thumbnails reuse the page-bitmap pipeline's low-resolution previews; ink is drawn as vectors on top (exact and cheap). Tabs: "Segni" (the 3b-1 list) and "Pagine".
- A PDF without a text layer: the search field shows the message once per document ("Nessun testo ricercabile in questo PDF") and the search does nothing.

## Review Focus

1. **Search correctness and noise**: accents, uppercase, ligatures (fi/fl), hyphenation at line ends (not joined: documented limit), a query spanning a line break, repeated matches in one line, matches on rotated pages and pages with a CropBox offset must highlight the right place; empty/whitespace query does nothing; a 1-char query on a 1,000-page book must not freeze (result cap with a message). Pinned in Tasks 1, 2.
2. **Search never hurts**: pen, scroll, save, render not delayed; pauses during stroke/save; a new query cancels the old scan without leaking results into the new one; document change/close cancels; no PDFium handle leaks (text page and find handles closed on every path); no unobserved exceptions. Pinned in Tasks 1, 2, 3, 6.
3. **Highlights in the right place and never over the ink**: rectangles in displayed page points convert only in the view; layer below the ink; correct after zoom, resize, rotation; current result stronger; cleared on Esc/new query/document change; not hit-testable. Pinned in Task 3.
4. **Thumbnails**: virtualised (1,000+ pages), bounded cache, ink included and up to date (draw/erase/undo), no stale ink after closing without saving and reopening, never blocks the UI thread or starves page renders, correct for pages of different sizes and rotations. Pinned in Task 5.
5. **Pen and 3a/3b-1 regressions**: the new tab strip, search field and list never take the pen; rail ticks of three kinds stay readable and non-hit-testable; marks, notes, scan, save, recovery suites unchanged. Pinned in Tasks 4, 5, 6.

---

### Task 1: PDFium text search (Pdf layer)

**Files:**
- Create: `src/DreamBooks.Pdf/Interop/PdfiumText.cs` (P/Invoke: `FPDFText_LoadPage`, `FPDFText_ClosePage`, `FPDFText_CountChars`, `FPDFText_GetText` (UTF-16LE), `FPDFText_GetUnicode`, `FPDFText_GetCharBox`, `FPDFText_GetCharIndexAtPos` if needed, `FPDFText_CountRects`/`GetRect` if used), `src/DreamBooks.Pdf/PdfTextSearcher.cs`
- Modify: `src/DreamBooks.Pdf/PdfRenderer.cs` (a `SearchPageAsync` entry that runs ONE job on the PDFium thread per page), `PageGeometry.cs` only if needed to map PDFium user-space boxes to displayed page points (top-left origin, y down, honouring /Rotate and CropBox/MediaBox, as the stroke code does)
- Core (pure): `src/DreamBooks.Core/Search/TextFolding.cs`, `FoldedText.cs`, `SearchRect.cs`, `SearchHit.cs`, `SearchPageResult.cs` (ALL result types live in Core.Search; Pdf returns them, Core never references Pdf)
- Test: `tests/DreamBooks.Core.Tests/Search/TextFoldingTests.cs`, `tests/DreamBooks.Pdf.Tests/PdfTextSearcherTests.cs`

**Interfaces:**
- Produces (Core, namespace `DreamBooks.Core.Search`): `static class TextFolding { static FoldedText Fold(string text); static string FoldQuery(string query); }` where `FoldedText(string Folded, int[] SourceIndex)` maps each folded char back to the source char index (lower-case invariant, accents removed via NFD with combining marks dropped, ligature characters expanded, runs of whitespace/newlines collapsed to a single space; the map must stay monotonic). `readonly record struct SearchRect(double LeftPt, double TopPt, double RightPt, double BottomPt)` (displayed page points). `sealed record SearchHit(int PageIndex, IReadOnlyList<SearchRect> Rects)` (one or more rectangles per hit: a hit crossing a line break has one per line; value equality by content).
- Produces (Pdf): `Task<SearchPageResult> PdfRenderer.SearchPageAsync(int pageIndex, string foldedQuery, int maxHits, CancellationToken ct)`; `sealed record SearchPageResult(bool HasTextLayer, IReadOnlyList<SearchHit> Hits, bool Truncated)`. One PDFium job per page: load page, load text page, read chars, fold, find all occurrences of the folded query, compute rectangles from per-character boxes (merge boxes on the same line), close the text page and the page on EVERY path; `HasTextLayer` = the page has at least one text char (a page with no text returns false and no hits; the caller decides whether the whole document has no layer). No page text is retained after the call.

- [ ] **Step 1: Failing tests.** Core `TextFoldingTests`: `Folding_lowercases_and_removes_accents`; `Ligatures_are_expanded`; `Whitespace_runs_and_line_breaks_collapse_to_one_space`; `SourceIndex_is_monotonic_and_maps_back_to_the_original_characters`; `Empty_and_whitespace_queries_fold_to_empty`; `Folding_is_culture_invariant` (run under a Turkish culture: "I" must fold to "i"). Pdf `PdfTextSearcherTests` (generated PDFs with a text layer written by `TestPdfFactory` or a hand-built content stream with `Tj` text; add a helper if missing): `Finds_a_word_and_returns_rectangles_in_displayed_page_points`; `Match_is_case_and_accent_insensitive`; `Two_occurrences_on_one_line_give_two_hits`; `A_query_spanning_a_line_break_gives_one_hit_with_two_rects`; `Rects_are_correct_on_a_page_with_Rotate_90_and_a_cropbox_offset` (compare against the same text on an upright page transformed by hand); `A_page_without_text_reports_no_text_layer`; `maxHits_truncates_and_sets_Truncated`; `Cancelled_token_returns_promptly_and_closes_handles` (use a handle-count seam or repeated calls not exhausting PDFium); `Search_does_not_change_the_saved_file` (search every page, save through the real saver, reopen, compare strokes and page content with `PdfFidelity`); real-copy test (skip when absent): searching a common short word over the 1,076-page copy completes, report only hit count and total time.
- [ ] **Step 2–4:** red → implement → green; run the Core and Pdf suites; format. **Step 5:** Commit `feat: PDFium text search with accent-insensitive matching and page-point rectangles (FR-13)`.

---

### Task 2: `SearchIndex` and `SearchScanner` (Core)

**Files:** Create `src/DreamBooks.Core/Search/SearchIndex.cs`, `ISearchPageSource.cs`, `SearchScanner.cs`, `SearchState.cs`; Test `tests/DreamBooks.Core.Tests/Search/SearchIndexTests.cs`, `SearchScannerTests.cs`.

**Interfaces:**
- `interface ISearchPageSource { int PageCount { get; } Task<SearchPageResult> SearchPageAsync(int pageIndex, string foldedQuery, int maxHits, CancellationToken ct); }` (PageCount cheap/cached; implemented in Task 3 over `PdfRenderer`; result types live in Core: move `SearchPageResult`/`SearchHit`/`SearchRect` to Core `Search` namespace in Task 1 or here, whichever keeps Pdf referencing Core only — decide and keep one definition).
- `sealed class SearchIndex(int pageCount)` thread-safe (copy-on-write like `MarkIndex`): `void SetPage(int pageIndex, IReadOnlyList<SearchHit> hits)`, `bool IsSearched(int)`, `int SearchedCount`, `int TotalHits`, `IReadOnlyList<SearchHit> AllHits()` (ordered by page then top), `IReadOnlyList<SearchHit> HitsOnPage(int)`, `bool TryNext(int pageIndex, double yPt, bool wrap, out SearchHit hit)` / `TryPrevious(...)` (strictly after/before the position, with wrap-around when asked), `event EventHandler<SearchIndexChangedEventArgs>? Changed`, `bool Truncated`.
- `sealed class SearchScanner(ISearchPageSource source, SearchIndex index, int maxTotalHits = 5000, Func<bool>? isBusy = null, TimeSpan? idleGap = null, TimeSpan? workSlice = null)`: `void Start(string rawQuery, int centrePage)` (folds the query, cancels any previous run, clears nothing in the old index: the owner creates a new `SearchIndex` per query), pages in the order centre, centre+1, … to the end, then 0 … centre−1 (wrap), `void Pause()`/`Resume()` (nesting), `void Dispose()`, `event EventHandler? Progress`, `bool IsCompleted`, `bool HasTextLayer` (false only after the scan finished with NO page having text; also set early if the first 20 pages scanned have none AND the document has more pages than that? — keep simple: decided only at completion), `int FailedCount`; same pacing model as `MarkScanner` after 3b-1 (one load in flight, rest after a slice of work, parks while `isBusy`), results from a stale run (older query) never reach the new index; hit cap sets `Truncated` and stops the scan.

- [ ] **Step 1: Failing tests:** `SearchIndex`: `Hits_are_ordered_by_page_then_top`; `TryNext_finds_the_following_hit_and_wraps_when_asked`; `TryNext_without_wrap_returns_false_after_the_last_hit`; `TryPrevious_mirror_cases`; `Identical_page_results_raise_no_Changed`; `Concurrent_SetPage_and_reads_are_safe`; `Truncated_flag_round_trips`. `SearchScanner` (fake source): `Pages_are_searched_from_the_centre_forward_then_wrapped`; `A_new_Start_cancels_the_old_run_and_stale_results_never_reach_the_new_index`; `Pause_and_isBusy_park_the_scan`; `The_cap_stops_the_scan_and_marks_the_index_truncated`; `A_failing_page_is_counted_and_scanning_continues`; `HasTextLayer_is_false_only_when_no_page_has_text_at_completion`; `Dispose_cancels_and_raises_nothing_afterwards`; `One_load_in_flight_at_most`; `No_unobserved_task_exceptions` (non-parallel collection).
- [ ] **Step 2–4:** red → implement → green; format. **Step 5:** Commit `feat: search index and background search scanner (FR-13, FR-105)`.

---

### Task 3: Search view model, UI and page highlights

**Files:** Create `src/DreamBooks.App/ViewModels/SearchViewModel.cs`, `src/DreamBooks.App/Views/SearchHighlightLayer.cs` (or equivalent); Modify `MainViewModel.cs` (own the `SearchIndex`/`SearchScanner` per query, source over the current `PdfRenderer` and its swap after a plain Save like the mark scanner), `DocumentViewportViewModel.cs` (`ScrollToHit(SearchHit, bool recordHistory)` using the same target rule as marks: centre the hit if it fits, else align its top), `InkSurface.cs` / `PageSurfacePool.cs` (a highlight layer BELOW the ink layer and ABOVE the page bitmap, fed per page by the view), `MainWindow.xaml(.cs)` (search box + count + previous/next buttons in the navigation panel, bindings for `Ctrl+F`, `F3`/`Shift+F3`, `Esc`), `Strings.resx` + `Strings.Designer.cs`, `Theme.xaml` (highlight colour and the stronger current-result colour; opaque variants not needed for a highlight but respect transparency setting if alpha is used); Test `tests/DreamBooks.App.Tests/SearchViewModelTests.cs`, `SearchHighlightTests.cs`.

**Interfaces:** `SearchViewModel`: `string QueryText { get; set; }` (debounced 300 ms through `IDelayScheduler` before a scan starts; empty clears), `string CountText` ("3 di 17", "17+" when truncated, "Nessun risultato" for zero, "Ricerca in corso…" while scanning with zero so far, resx), `bool HasResults`, `bool IsSearching`, `string? NoTextLayerMessage`, `IRelayCommand NextCommand`, `IRelayCommand PreviousCommand` (wrap with a 2 s status "Ripartito dall'inizio" / "dalla fine" via the existing scheduler), `IRelayCommand ClearCommand`, `SearchHit? Current`, `IReadOnlyList<SearchRect> HitsForPage(int pageIndex)` + `CurrentRectsForPage(int)`, `event EventHandler<int>? PageHighlightsChanged`. Starting point for next/previous = viewport position (same rule as marks: target position, 15% of window band). Jumps record Back history once; commands never depend on `IsSaving`. The scan pauses while a stroke is live (ZoomVm.IsLocked) and during a save; a new document cancels and clears; the scan follows the renderer swap after a plain Save without losing hits already found.

- [ ] **Step 1: Failing tests:** `Typing_starts_a_search_after_the_debounce_and_clears_on_empty`; `Count_text_formats_for_zero_some_truncated_and_scanning`; `Next_goes_to_the_following_hit_records_history_and_previous_returns`; `Next_wraps_with_a_status_message`; `A_new_query_replaces_the_results_and_highlights_of_the_old_one`; `Esc_or_Clear_removes_results_highlights_and_returns_focus`; `No_text_layer_shows_the_message_once_per_document`; `Search_pauses_while_a_stroke_is_live_and_during_a_save`; `Renderer_swap_after_Save_keeps_the_hits_found_and_continues`; `Opening_another_document_cancels_the_search`; `Commands_stay_enabled_while_saving`; `Ctrl_F_focuses_the_box_and_Enter_Shift_Enter_F3_navigate` (via the `MainWindow.RouteKey`-style seam); `Highlight_layer_draws_rects_converted_from_page_points_at_the_surface_scale_and_sits_below_the_ink_layer` (STA); `Highlight_layer_is_not_hit_testable_and_the_pen_still_reaches_the_ink_surface`; `Current_result_uses_the_stronger_brush`; `Highlights_follow_zoom_and_page_rotation`; `Strings_Designer_stays_in_sync`.
- [ ] **Step 2–4:** red → implement → green; full suite; format. **Step 5:** Commit `feat: text search UI with page highlights, result navigation and history (FR-13, FR-105)`.

---

### Task 4: Search ticks on the rail

**Files:** Modify `src/DreamBooks.Core/Marks/RailTickLayout.cs` (or the neutral place it lives) to compute positions for a third kind (`SearchPositions(...)` using the same mapping as the knob and the same jump target rule), `src/DreamBooks.App/Views/ScrollRail.xaml(.cs)`, `RailTickLayer.cs`, `Theme.xaml` (search tick brush + opaque variant, offset so the three kinds stay visible when coinciding: marks centred, notes left (existing), search on the right); Test `tests/DreamBooks.Core.Tests/Marks/RailTickLayoutSearchTests.cs`, `tests/DreamBooks.App.Tests/ScrollRailSearchTicksTests.cs`.

**Behaviour:** one tick per merged position (within 1 DIP) for `SearchIndex.AllHits()`; refreshed when the index changes (throttled like marks); cleared when the search is cleared; non-hit-testable, no automation peer, drawn below the knob; opaque when transparency is off; a truncated search draws what it has.

- [ ] **Step 1: Failing tests:** `Search_ticks_use_the_same_mapping_as_the_knob_and_marks`; `Ticks_merge_within_one_dip`; `Clearing_the_search_removes_the_ticks`; `Three_kinds_remain_visible_when_they_coincide`; `Ticks_are_not_hit_testable_and_the_pen_still_inks`; `Opaque_brush_when_transparency_is_off`; `Thousands_of_hits_produce_one_geometry`.
- [ ] **Step 2–4:** red → implement → green; full suite; format. **Step 5:** Commit `feat: search result ticks on the scroll rail (FR-105, UI-07)`.

---

### Task 5: "Pagine" tab with thumbnails that include the ink

**Files:** Create `src/DreamBooks.App/Views/PagesListView.xaml(.cs)`, `src/DreamBooks.App/ViewModels/PagesViewModel.cs`, `src/DreamBooks.App/Views/PageThumbnailCache.cs`; Modify `EdgePanel.xaml(.cs)` (a tab strip "Segni" / "Pagine", pen-transparent like the rest, tab buttons 40 px, `AutomationProperties.Name`), `MainWindow.xaml(.cs)` (opening shortcut for the Pages tab: `Ctrl+Shift+M`? choose a free one, e.g. `Ctrl+P` — check clashes; menu entry), `PageBitmapCoordinator.cs` (a low-priority thumbnail request path that goes through the existing scheduler or a dedicated small queue: **thumbnail renders never starve visible-page renders**), `Strings.resx` + `Strings.Designer.cs`, `Theme.xaml`; Test `tests/DreamBooks.App.Tests/PagesViewModelTests.cs`, `PagesListViewTests.cs`, `PageThumbnailCacheTests.cs`.

**Behaviour:** a virtualised vertical list (`VirtualizingStackPanel`, fixed item heights per page aspect ratio computed from `DocumentLayout` page sizes, one row per page, 140 DIP wide thumbnails, page number under each); the current page ringed (theme token); pages having marks / notes show a small tick in the corresponding colour; tap = go to page (history recorded, panel stays open); opening the tab scrolls the list so the current page is visible; thumbnails = a low-resolution page bitmap (about 140 DIP wide at the screen scale) drawn first, plus the page's ink as vectors on top (strokes for loaded pages from the session, others through the uncached read used by the mark scan; a stroke change on a page redraws its thumbnail ink); caches: bitmaps ≤ 40 MB budget with LRU, ink thumbnails ≤ 64 entries, both cleared on document change/reopen (no stale ink after "Non salvare"), loads only for realised rows, cancelled on recycle, off the UI thread, exceptions never unobserved; placeholder (white page-shaped box with the number) until the bitmap arrives; never a blank crash for a page that fails to render.

- [ ] **Step 1: Failing tests:** `Tab_strip_switches_between_marks_and_pages_and_keeps_the_panel_pen_transparent`; `Pages_list_has_one_row_per_page_with_the_correct_aspect_ratio_for_mixed_sizes`; `Current_page_is_ringed_and_follows_the_viewport`; `Tap_goes_to_the_page_records_history_once_and_keeps_the_panel_open`; `Opening_the_tab_scrolls_to_the_current_page`; `List_virtualises_a_thousand_pages_and_realises_only_visible_rows`; `Thumbnail_requests_are_low_priority_and_cancelled_on_recycle` (fake coordinator); `Thumbnail_cache_respects_its_budget_and_evicts_least_recently_used`; `Ink_is_drawn_over_the_bitmap_and_follows_draw_erase_undo`; `Caches_are_cleared_on_reopen_without_saving`; `Pages_with_marks_or_notes_show_ticks`; `A_page_that_fails_to_render_keeps_its_placeholder_and_raises_nothing`; `Pen_press_on_the_closed_edge_still_inks_and_on_the_open_sheet_closes_it` (same rule as 3b-1); `Strings_Designer_stays_in_sync`.
- [ ] **Step 2–4:** red → implement → green; full suite; format. **Step 5:** Commit `feat: pages tab with thumbnails that include the ink (FR-103, UI-04)`.

---

### Task 6: Integration, large document and regression sweep

**Files:** Test `tests/DreamBooks.App.Tests/SearchAndPagesIntegrationTests.cs`; fixes anywhere the tests expose problems (TDD in the owning layer).

**Behaviour to pin (real `MainViewModel`, `DocumentView`, PDFium; generated PDFs with a text layer; real 1,076-page copy with skip):**
- Generated 300-page PDF with known words on known pages (plus accents/case variants): the search finds exactly those hits, ticks equal hits, next/previous visit them in order with wrap, Back returns, highlights appear on the visible page and disappear on Esc.
- Search while the mark scan is running and while editing/saving: both scans park and resume, nothing is lost, a save mid-search keeps the hits (renderer swap), the saved file is unchanged by the search (round trip with `PdfFidelity`, strokes, annotations, page content).
- A stroke committed during a search is processed in a normal time (record the number); a new query cancels the old one without stale hits.
- Real copy: time for a common word and for a rare word over all pages (numbers only), peak private memory delta (< 100 MB), no handle growth (PDFium thread alive, repeated full searches stable), scan never more than one job in flight; report if the full search exceeds ~10 s.
- Pages tab with 1,076 pages: opening and scrolling the list realises only visible rows; thumbnail cache within budget; memory delta reported; scrolling the document while thumbnails load does not delay page renders (measure first-visible-page time with the panel open).
- Regression: pen/finger/eraser gating, rail pen rules (knob grab, strip inks), mark/note scan and ticks, panel rules of 3b-1, undo/redo, journal, recovery, save pipeline all green; `dotnet format` clean.

- [ ] **Step 1:** write the tests; **Step 2:** run, fix failures test-first; **Step 3:** full suite twice + format; **Step 4:** Commit `test: search and pages integration, large document and regression sweep (FR-13, FR-105, FR-103, NFR-06)`.

---

### Task 7: Documents, publish, checklist (controller)

- [ ] Update `CLAUDE.md` project structure (Core/Search, Pdf text search, App SearchViewModel/SearchHighlightLayer/PagesListView/PageThumbnailCache); `docs/requirements.md` only where the build differs from FR-13/FR-105/FR-103/UI-04 (shortcuts Ctrl+F, F3, the Pages tab shortcut); publish to a NEW folder (`publish-3b2`); give Fausto an Italian manual checklist: search (accents, uppercase, a word that spans a line, a word that is not there, a very common word), highlights position at several zooms and on a rotated page, ticks of three colours, next/previous with F3 and Enter, wrap message, Esc, searching while writing, searching a scanned page (no text), the Pages tab (ink in thumbnails, current page ring, tap to go, tick marks, scrolling 1,000 pages, memory), the pen rules, Windows animations/transparency off, plus the still-open 3a/3b-1 items. Wait for his results before merging.
