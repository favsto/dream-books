# Phase 3b-1 Marks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The app recognises Fausto's margin lines as *marks*, shows them as ticks on the scroll rail, jumps to the previous/next mark (with Back history) and lists them in a small left-edge panel — without writing anything new into the PDF.

**Architecture:** Core gets pure, tested code: `MarkRecognizer` (strokes of a page → marks), `MarkIndex` (all pages, ordered, neighbour lookup), `MarkScanner` (background scan order/pause/cancel over an `IMarkPageSource`) and `RailTickLayout` (tick merging). A `MarkTracker` binds the scanner/index to `DocumentSession` so loaded pages update live. Pdf provides a cheap "does this page hold our annotations" pre-check. App adds `MarksViewModel`, ticks on `ScrollRail`, previous/next commands, and a reusable left-edge panel shell hosting the marks list.

**Tech Stack:** unchanged (.NET 10, WPF, PDFium, CommunityToolkit.Mvvm, xUnit). No new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-10-31-phase3b-finding-aids-design.md` (sections 4, 5, 6 (marks parts) and 10 are binding for this stage). Builds on 3a (`docs/superpowers/specs/2026-10-31-phase3a-multipage-viewer-design.md`, still not tested on the device: do not regress it).

## Global Constraints

- Core never references WPF/PDFium; PDFium only on the PDFium thread; the UI thread only handles input and drawing (NFR-04); stroke model and JSON schema v1 unchanged; **nothing new is written into the PDF or the journal**; marks are derived data.
- The scan runs at idle priority: never delays the pen, scrolling, a render or a save; pauses during a stroke and during a save; cancels on document change/close; no unobserved task exceptions; memory bounded (keep marks, not strokes, for unloaded pages).
- Italian text only in `Strings.resx` (+ hand-written `Strings.Designer.cs`, drift test); values/colours/durations in `Theme.xaml`; NFR-32 (reduced animation/transparency honoured by the new panel and ticks); 40 px touch targets; `AutomationProperties.Name` on every command; keyboard reachable; **the pen never hits the panel, the ticks or the new buttons' regions it did not already reach** (same rule as the 3a rail: finger/mouse only).
- Shortcuts: `Alt+Up` previous mark, `Alt+Down` next mark (free in 3a: only Alt+Left, PgUp/PgDn/Home/End, Ctrl +/- exist).
- Nullable on; file-scoped namespaces; one public type per file named after the type; `_` prefix for private static readonly/const; warnings are errors; TDD in Core/Pdf; view models unit-tested; never run the WPF app; do not stage `.claude/settings.json`; commit trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`; `dotnet build` 0 warnings, `dotnet test` green, `dotnet format --verify-no-changes` clean at the end of every task.
- Never log document content. Real-book tests: copies only in `C:\Users\Favsto\workspace\temp` (or `DREAMBOOKS_FIXTURES`), skip when absent, report only counts and times.
- Rule constants live in one class `MarkRules` (defaults from spec section 4): max deviation from vertical 20°, path length ≤ 1.3× end-to-end distance, minimum vertical extent 3 text lines with line height fixed at **36 pt** in 3b-1, outer margin zone = outer 18% of page width on either side, merge gap ≤ 1.5 lines, margin-note window ±1 line and within 25% of page width of the zone. Prefer a few false marks over missed ones.

## Rulings made while planning (reversible)

- Text-layer metrics (real line height, text block bounds) are **not** used in 3b-1; fixed 36 pt and the 18% zone are. They arrive in 3b-2 together with the PDFium text P/Invoke. The recogniser takes an `IPageLineMetrics` seam (default fixed implementation) so 3b-2 swaps it without touching callers.
- The `IMarkSource` seam for the highlighter (spec section 4) is **not built** in 3b-1 (YAGNI): `MarkIndex.SetPage` already accepts marks from any producer, so 3c adds its recogniser without touching consumers.
- Tick snapping of the rail knob to the nearest mark (spec 6, "optional") is **not built**.
- The marks list shows page number, the vector ink of the margin region and the margin note ink; a crop of the page bitmap behind it arrives with the 3b-2 thumbnail pipeline.
- The persisted mark-index cache is a **conditional** Task 9, built only if the real-book scan (Task 8) exceeds ~5 s.

## Review Focus

1. **Wrong marks**: a short line, a tilted stroke over 20°, a zig-zag/scribble, a vertical stroke in the middle of the text, a highlighter stroke and an eraser stroke must NOT become marks; a wavy-but-vertical line near the margin MUST. Pinned in Task 1.
2. **Live consistency**: draw a line → tick appears; erase it, undo, redo → the tick follows; two lines on one passage = one mark; a line crossing another mark's gap merges. Pinned in Tasks 1, 3.
3. **Scan never hurts**: pen, scroll, save and render are not delayed; paused during a stroke/save; cancelled on close; unobserved exceptions none; pages with no annotations are skipped cheaply; a damaged page does not stop the scan. Pinned in Tasks 3, 4, 8.
4. **Jump semantics**: previous/next from any scroll position (inside a mark, between marks, before the first, after the last, wrap or stop — decided in Task 2: stops at the ends and says so), history recorded once, no jump when there are no marks. Pinned in Tasks 2, 5.
5. **Pen safety and 3a regressions**: ticks and the new panel never intercept the pen; rail fade/behaviour, go-to-page, Back, save and recovery suites unchanged. Pinned in Tasks 6, 7, 8.

---

### Task 1: `MarkRecognizer` — strokes to marks

**Files:**
- Create (namespace `DreamBooks.Core.Marks`): `src/DreamBooks.Core/Marks/Mark.cs`, `MarkSide.cs`, `MarkRules.cs`, `IPageLineMetrics.cs`, `FixedLineMetrics.cs`, `MarkRecognizer.cs`
- Test: `tests/DreamBooks.Core.Tests/Marks/MarkRecognizerTests.cs`

**Interfaces:**
- Produces: `enum MarkSide { Left, Right }`; `sealed record Mark(int PageIndex, MarkSide Side, double TopPt, double BottomPt, IReadOnlyList<Guid> StrokeIds)` (page points, top-left origin, y down, as displayed); `static class MarkRules` (constants above, public `const`/static readonly, documented); `interface IPageLineMetrics { double LineHeightPt(int pageIndex); }`; `sealed class FixedLineMetrics(double lineHeightPt = 36) : IPageLineMetrics`; `sealed class MarkRecognizer(IPageLineMetrics? metrics = null)` with `IReadOnlyList<Mark> Recognize(int pageIndex, double pageWidthPt, IReadOnlyList<InkStroke> strokes)` returning merged marks ordered by `TopPt`. Also `IReadOnlyList<InkStroke> MarginNotes(Mark mark, double pageWidthPt, IReadOnlyList<InkStroke> strokes)` returning the non-mark strokes within the margin-note window.

- [ ] **Step 1: Failing tests** (build strokes with a small helper from points; `InkStroke(Guid, InkTool, InkColor, double, IReadOnlyList<InkPoint>)` exists):

```csharp
[Fact] public void A_straight_vertical_pen_line_in_the_margin_longer_than_three_lines_is_a_mark()
[Fact] public void A_wavy_but_vertical_line_within_the_tolerance_is_a_mark()                      // path ≤ 1.3x, ≤ 20°
[Fact] public void A_tilted_line_over_twenty_degrees_is_not_a_mark()
[Fact] public void A_zig_zag_or_scribble_is_not_a_mark()                                         // path length > 1.3x
[Fact] public void A_line_shorter_than_three_lines_is_not_a_mark()
[Fact] public void A_vertical_line_in_the_middle_of_the_page_is_not_a_mark()
[Fact] public void Left_and_right_margins_are_both_recognised_and_report_their_side()
[Fact] public void Highlighter_strokes_are_never_margin_lines()                                  // InkTool.Highlighter
[Fact] public void Two_overlapping_or_close_lines_merge_into_one_mark_with_the_union_extent()   // gap ≤ 1.5 lines
[Fact] public void Lines_farther_apart_than_one_and_a_half_lines_stay_separate_marks()
[Fact] public void Lines_on_opposite_sides_never_merge()
[Fact] public void Marks_are_ordered_by_top()
[Fact] public void A_stroke_with_fewer_than_two_points_is_ignored()
[Fact] public void Margin_notes_are_the_non_mark_strokes_near_the_mark_within_the_window()
[Fact] public void Custom_line_metrics_change_the_minimum_extent()                                // IPageLineMetrics seam
```

- [ ] **Step 2:** `dotnet test tests/DreamBooks.Core.Tests --filter MarkRecognizerTests` → FAIL (types missing). **Step 3:** Implement per spec section 4 (use the stroke's first/last point and total path length; "within 20° of vertical" via atan; zone test on the stroke's mean x; merge greedily after sorting by top). **Step 4:** green, format. **Step 5:** Commit `feat: mark recogniser for margin lines (FR-100, FR-104)`.

---

### Task 2: `MarkIndex` — document-wide marks and neighbour lookup

**Files:** Create `src/DreamBooks.Core/Marks/MarkIndex.cs`, `MarkLocation.cs`; Test `tests/DreamBooks.Core.Tests/Marks/MarkIndexTests.cs`.

**Interfaces:**
- Consumes: `Mark`.
- Produces: `readonly record struct MarkLocation(int PageIndex, double TopPt, double BottomPt)`; `sealed class MarkIndex(int pageCount)` thread-safe (lock or immutable snapshots): `void SetPage(int pageIndex, IReadOnlyList<Mark> marks)` (replaces that page's marks; raises `Changed` only if they differ), `void ClearPage(int pageIndex)`, `bool IsScanned(int pageIndex)`, `int ScannedCount`, `int TotalMarkCount`, `IReadOnlyList<Mark> AllMarks()` (ordered by page then top), `IReadOnlyList<Mark> MarksOnPage(int)`, `bool TryNext(int pageIndex, double yPt, out Mark mark)` / `TryPrevious(...)` (strictly after/before the position: next = first mark with (page, top) > (page, y); previous = last mark with top < y minus a 1 pt tolerance so repeated "previous" walks back; **stops at the ends, no wrap**), `event EventHandler<MarkIndexChangedEventArgs>? Changed` (carries the page index), `void Reset(int pageCount)` for a new document.

- [ ] **Step 1: Failing tests:** `SetPage_orders_marks_by_page_then_top`; `Replacing_a_page_raises_Changed_once_and_identical_marks_do_not`; `TryNext_from_inside_a_mark_goes_to_the_following_one`; `TryNext_returns_false_after_the_last_mark`; `TryPrevious_returns_false_before_the_first_mark`; `Repeated_previous_walks_back_through_every_mark`; `Neighbour_lookup_skips_unscanned_pages_without_failing`; `ScannedCount_and_TotalMarkCount_track_updates`; `Concurrent_SetPage_and_reads_are_safe` (parallel loop, no exception, consistent final state); `Reset_clears_everything_and_changes_page_count`; `Ten_thousand_marks_lookup_is_fast` (binary search; 10,000 lookups < 50 ms after warm-up).
- [ ] **Step 2–4:** red → implement → green; format. **Step 5:** Commit `feat: document-wide mark index with neighbour lookup (FR-101, FR-102)`.

---

### Task 3: `MarkScanner` and `MarkTracker` — background scan and live updates

**Files:** Create `src/DreamBooks.Core/Marks/IMarkPageSource.cs`, `MarkScanner.cs`, `MarkTracker.cs`; Modify `src/DreamBooks.Core/Documents/DocumentSession.cs` (event for stroke changes on loaded pages); Test `tests/DreamBooks.Core.Tests/Marks/MarkScannerTests.cs`, `MarkTrackerTests.cs`, `tests/DreamBooks.Core.Tests/Documents/DocumentSessionPageEventsTests.cs`.

**Interfaces:**
- `interface IMarkPageSource { int PageCount { get; } PageDimensions GetPageDimensions(int pageIndex); Task<PageStrokes?> TryLoadStrokesForScanAsync(int pageIndex, CancellationToken ct); }` — returns `null` when the page certainly holds none of our annotations (cheap pre-check, Task 4), otherwise the strokes. Never throws for expected failures (damaged page → `null` plus the scanner counts it as skipped).
- `DocumentSession` gains `event EventHandler<PageStrokesChangedEventArgs>? PageStrokesChanged` (`PageIndex`), raised when a loaded page's `Page.StrokesChanged` fires and when a page finishes loading; unsubscribed on `Dispose`; `Page` objects of unloaded pages are not subscribed. Add `PageStrokesChangedEventArgs` in `Documents`.
- `sealed class MarkScanner(IMarkPageSource source, MarkRecognizer recognizer, MarkIndex index, Func<bool>? isBusy = null, TimeSpan? idleGap = null)`: `void Start(int centrePage)`, `void Recentre(int centrePage)` (re-orders the remaining queue: pages nearest the centre first, alternating outward), `void Pause()`/`Resume()` (used while inking or saving, also driven by `isBusy`), `Task WhenIdleAsync()` (tests), `event EventHandler? Progress`, `void Dispose()` (cancels). One page at a time; yields between pages (`Task.Delay(idleGap, ct)` with default 5 ms, configurable to zero in tests); pages already set by live updates are skipped; exceptions never escape (counted, ignored).
- `sealed class MarkTracker(DocumentSession session, MarkRecognizer recognizer, MarkIndex index)`: subscribes to `session.PageStrokesChanged`, recomputes marks for that page from `session.EnsurePageAsync`'s strokes (no PDFium load: the page is loaded) and updates the index; `void Dispose()`.

- [ ] **Step 1: Failing tests:**

```csharp
// MarkScannerTests (fake IMarkPageSource with gates, counters, injectable clock/gap)
[Fact] public async Task Pages_are_scanned_nearest_the_centre_first_alternating_outward()
[Fact] public async Task Recentre_reorders_the_remaining_queue()
[Fact] public async Task Pages_with_no_annotations_are_skipped_without_recognition()           // null from the source
[Fact] public async Task A_page_that_throws_or_returns_garbage_is_skipped_and_scanning_continues()
[Fact] public async Task Pause_stops_after_the_current_page_and_Resume_continues()
[Fact] public async Task isBusy_pauses_the_scan_while_true()
[Fact] public async Task Dispose_cancels_the_scan_and_no_events_are_raised_afterwards()
[Fact] public async Task Pages_already_updated_live_are_not_overwritten_by_the_scan()
[Fact] public async Task A_thousand_page_document_scans_to_completion_with_bounded_concurrency()   // never more than one load in flight
[Fact] public async Task No_unobserved_task_exceptions()                                          // non-parallel collection like the scheduler tests
// MarkTrackerTests (real DocumentSession over FakePageSource-like fake)
[Fact] public async Task Committing_a_margin_line_adds_a_mark_for_that_page()
[Fact] public async Task Erasing_the_line_removes_the_mark_and_undo_brings_it_back()
[Fact] public async Task Two_lines_on_the_same_passage_are_one_mark()
[Fact] public async Task Pages_loaded_later_are_recognised_when_they_finish_loading()
[Fact] public async Task Dispose_unsubscribes()
// DocumentSessionPageEventsTests
[Fact] public async Task PageStrokesChanged_fires_for_edits_undo_redo_and_page_load()
[Fact] public async Task No_event_after_Dispose()
```

- [ ] **Step 2–4:** red → implement → green; full Core suite (existing session tests unchanged); format. **Step 5:** Commit `feat: background mark scan and live mark tracking (FR-100, FR-101)`.

---

### Task 4: Cheap annotation pre-check and the real page source (Pdf)

**Files:** Modify `src/DreamBooks.Pdf/PdfRenderer.cs` (and Interop if a new PDFium call is needed); Create `src/DreamBooks.Pdf/MarkPageSource.cs`; Test `tests/DreamBooks.Pdf.Tests/MarkPageSourceTests.cs`.

**Interfaces:** `sealed class MarkPageSource(PdfRenderer renderer) : IMarkPageSource` (Pdf references Core). `TryLoadStrokesForScanAsync` first asks the PDFium thread for the page's annotation count / our-key presence in ONE job (open page, count annotations, for ink annotations check our private key, close page); `null` when none; otherwise loads strokes through the existing `LoadStrokesAsync` path (re-using the first-touch stroke cache). It must not parse JSON for pages that only carry foreign annotations, and must not hold a page open longer than needed. Report the measured cost per page in the test output only as numbers.

- [ ] **Step 1: Failing tests:** `Page_without_annotations_returns_null_without_loading_strokes` (generated PDF); `Page_with_only_foreign_annotations_returns_null` (a link annotation / text annotation from `TestPdfFactory` or a hand-built PDF); `Page_with_our_strokes_returns_them_identical_to_LoadStrokesAsync`; `Damaged_annotation_json_returns_a_page_with_unreadable_count_not_an_exception`; `Cancelled_token_returns_promptly`; `Scan_of_a_300_page_generated_document_with_strokes_on_twenty_pages_finds_exactly_those_twenty` ; real-copy test (skip when absent): scan time for all pages of the 1,076-page book printed as a number, asserting only that it completes.
- [ ] **Step 2–4:** red → implement → green; run the Pdf suite; format. **Step 5:** Commit `feat: cheap annotation pre-check and mark page source (FR-100, NFR-02)`.

---

### Task 5: `MarksViewModel` — list, previous/next, history

**Files:** Create `src/DreamBooks.App/ViewModels/MarksViewModel.cs`, `MarkItem.cs`; Modify `MainViewModel.cs` (own the `MarkIndex`/`MarkScanner`/`MarkTracker` lifecycle per document: start after open, `Recentre` when the current page changes after a jump, pause while inking/saving, dispose on document change/close), `DocumentViewportViewModel.cs` (a `ScrollToMark(Mark, bool recordHistory)` that centres the extent if it fits the window, else aligns its top; reuse `ScrollToPage` plumbing and `Jumped`), `Strings.resx` + `Strings.Designer.cs`; Test `tests/DreamBooks.App.Tests/MarksViewModelTests.cs`.

**Interfaces:** `MarksViewModel`: `IReadOnlyList<MarkItem> Items` (observable, rebuilt from `MarkIndex.AllMarks()` on `Changed`, throttled to one update per 100 ms through the existing injectable `IDelayScheduler`), `int Count`, `bool IsEmpty`, `bool IsScanning`, `string EmptyText` (resx: line vertical on the side, at least 3 lines), `IRelayCommand NextCommand`, `IRelayCommand PreviousCommand` (from the viewport's current position: page of the top edge + YPt; stop at the ends: set `StatusText` "Nessun altro segno" for 2 s, no jump), `IRelayCommand<MarkItem> GoToMarkCommand` (records history). `MarkItem(Mark Mark, string PageLabel)` where `PageLabel` is resx "pag. {0}". All navigation commands stay enabled while saving.

- [ ] **Step 1: Failing tests:** `Items_follow_the_index_and_are_ordered`; `Index_changes_are_throttled_to_one_rebuild_per_interval`; `Next_goes_to_the_following_mark_records_history_once_and_back_returns`; `Previous_goes_to_the_preceding_mark`; `Next_at_the_last_mark_does_not_jump_and_reports_no_other_mark`; `Previous_at_the_first_mark_does_not_jump`; `Commands_do_nothing_when_there_are_no_marks`; `GoToMark_centres_a_short_mark_and_aligns_the_top_of_a_tall_one`; `Opening_a_document_starts_the_scan_and_closing_disposes_it`; `Scan_pauses_while_a_stroke_is_live_and_during_a_save`; `Recentre_follows_a_jump_to_a_distant_page`; `Strings_Designer_stays_in_sync_with_the_new_keys` (existing drift test); existing MainViewModel/Viewport/Save/Recovery tests pass.
- [ ] **Step 2–4:** red → implement → green; full suite; format. **Step 5:** Commit `feat: marks view model with previous/next mark and history (FR-102, FR-103)`.

---

### Task 6: Rail ticks

**Files:** Create `src/DreamBooks.Core/Marks/RailTickLayout.cs`; Modify `src/DreamBooks.App/Views/ScrollRail.xaml(.cs)`, `Theme.xaml` (tick brush/size tokens, opaque variants for NFR-32); Test `tests/DreamBooks.Core.Tests/Marks/RailTickLayoutTests.cs`, `tests/DreamBooks.App.Tests/ScrollRailTicksTests.cs`.

**Interfaces:** `static class RailTickLayout { static IReadOnlyList<double> Positions(IReadOnlyList<Mark> marks, DocumentLayout layout, double trackLengthDip, double mergeDip = 1) }` returns sorted tick offsets in DIP along the track (mark top mapped through `DocumentLayout.PageTopPt` + `TopPt`, scaled to the track the same way the knob is: offset/(extent−viewport) is NOT used for ticks; ticks use the document fraction so they line up with the knob's range, as decided in 3a: **check how `ScrollRail` maps knob position and use the identical mapping**), ticks closer than `mergeDip` merged. `ScrollRail` draws ticks (non-hit-testable, drawn under the knob) only while visible; refreshes when the index changes; nothing is drawn when there are no marks; the tick layer never takes the pen (the 3a rule stays: no part of the rail is hit-testable).

- [ ] **Step 1: Failing tests:** `Positions_map_mark_tops_to_the_track_with_the_same_mapping_as_the_knob`; `Ticks_closer_than_one_dip_merge`; `One_page_document_and_empty_marks_give_empty_or_single_tick_without_division_by_zero`; `Ten_thousand_marks_produce_at_most_one_tick_per_dip`; App STA: `Rail_draws_one_tick_per_merged_position`, `Ticks_are_not_hit_testable_and_the_pen_still_reaches_the_ink_surface`, `Ticks_use_opaque_brushes_when_transparency_is_off`, `Ticks_appear_and_vanish_with_the_index_updates`; automation name for the tick layer is not needed (decorative) — assert it is excluded from the automation tree.
- [ ] **Step 2–4:** red → implement → green; full suite; format. **Step 5:** Commit `feat: mark ticks on the scroll rail (FR-101, UI-07)`.

---

### Task 7: Left-edge panel shell and the marks list; previous/next in the UI

**Files:** Create `src/DreamBooks.App/Views/EdgePanel.xaml(.cs)` (reusable shell: title, close button, slide in/out from the left, reduced-animation instant, pen-transparent), `MarksListView.xaml(.cs)`, `MarkInkThumbnail.cs` (draws the margin region's ink as vectors from the stroke contours at thumbnail scale; uses existing `StrokeContour`); Modify `MainWindow.xaml(.cs)` (host the panel, buttons Previous/Next mark in the navigation panel next to Back/go-to-page, KeyBindings Alt+Up/Alt+Down, menu entry to open the list, swipe-from-left-edge opening by finger only), `Strings.resx` + Designer, `Theme.xaml`; Test `tests/DreamBooks.App.Tests/EdgePanelTests.cs`, `MarksListViewTests.cs`.

**Behaviour:** the panel opens with a finger swipe from the left 24 px edge (same routing rule as the rail: finger/mouse only, a pen at the edge inks), from the menu entry and a button; closes with swipe, tap outside, Esc, or the close button; width 320 DIP; auto-hides after a jump from the list; rows: ink thumbnail (≈ 120×60 DIP), "pag. N", count in the header, empty text from `MarksViewModel.EmptyText`; tap row = `GoToMarkCommand`; rows virtualised (`VirtualizingStackPanel`); keyboard: arrows/Enter. Previous/Next buttons 40×40, `AutomationProperties.Name` Italian.

- [ ] **Step 1: Failing tests (STA):** `Panel_opens_and_closes_by_command_and_Esc`; `Panel_open_state_animates_unless_animations_are_off`; `Pen_at_the_left_edge_inks_and_does_not_open_the_panel`; `Finger_swipe_from_the_edge_opens_it`; `Tap_outside_closes_it`; `Marks_list_shows_one_row_per_mark_with_page_label`; `Row_tap_invokes_GoToMark_and_closes_the_panel`; `Empty_state_text_is_shown_when_there_are_no_marks`; `Thumbnail_draws_the_margin_ink_scaled_to_the_row` (render to a bitmap and assert some non-background pixels in the expected area); `List_virtualises_a_thousand_marks`; `Previous_next_buttons_and_Alt_Up_Down_invoke_the_commands` (via the `MainWindow.RouteKey`-style seam from 3a); `MainWindow_instantiates_with_the_panel`; `Strings_Designer_stays_in_sync`.
- [ ] **Step 2–4:** red → implement → green; full suite; format. **Step 5:** Commit `feat: left-edge panel with the marks list and previous/next mark controls (FR-102, FR-103, UI-04)`.

---

### Task 8: Integration, real book and regression sweep

**Files:** Test `tests/DreamBooks.App.Tests/MarksIntegrationTests.cs`; fixes anywhere the tests expose problems (TDD).

**Behaviour to pin (real `MainViewModel`, `DocumentView`, PDFium; generated PDFs; real e-book copy with skip):**
- Generated 300-page PDF with margin lines on 12 known pages (saved through the real saver, reopened): after open and `WhenIdle`, the index holds exactly those 12 marks (+ merged cases), ticks equal marks, next/previous visit them in order and Back returns.
- Draw a margin line live on a visible page: tick appears without a scan; erase/undo/redo follow.
- Scan does not hurt: during a long scan, a stroke commit is processed within a normal bound, a save completes, scrolling continues, the scan pauses during the save and resumes; closing the document mid-scan leaves no unobserved exception and no leaked subscription.
- Damaged page in the middle: scan completes, others still marked.
- Real 1,076-page copy (skip when absent): scan time, number of pages with annotations, private-memory delta (< 100 MB for the index and scanner), no more than one load in flight; report numbers only. **If the scan exceeds ~5 s, report it: Task 9 becomes mandatory.**
- Regression: pen/finger/eraser gating, 3a rail/pen routing tests, zoom lock, undo/redo, journal, recovery, save pipeline all green; format clean.

- [ ] **Step 1:** write the tests; **Step 2:** run, fix failures test-first in the owning layer; **Step 3:** full suite twice + format; **Step 4:** Commit `test: marks integration, large document scan and regression sweep (FR-100..FR-103, NFR-06)`.

---

### Task 9 (conditional): Persisted mark-index cache

Only if Task 8 reports a real-book scan above ~5 s (otherwise skip and note it in the ledger).

**Files:** Create `src/DreamBooks.Pdf/MarkIndexStore.cs`; Modify `MainViewModel` (load before the scan, save when the scan completes and on close); Test `tests/DreamBooks.Pdf.Tests/MarkIndexStoreTests.cs`.

**Interface:** `MarkIndexStore(string root, Func<DateTime>? clock = null)` (production root `%LOCALAPPDATA%\DreamBooks\marks`): `StoredMarks? TryLoad(string documentPath, DocumentFingerprint fingerprint)`, `void Save(string documentPath, DocumentFingerprint fingerprint, IReadOnlyList<Mark> marks, int scannedPages)`, `Prune(...)` like positions. Versioned JSON (`"v":1`), ASCII only, atomic write, corrupt/unknown version/fingerprint mismatch → null, `Save` never throws (reports through `Failed`), no document content or path inside. A loaded cache is treated as complete only for pages whose stroke counts it recorded; pages edited since (fingerprint differs) trigger a full rescan.

- [ ] Tests: round trip; fingerprint mismatch → null; corrupt → null; atomic under concurrent readers; prune by age and count; never writes for a failed open; cache hit makes the scan finish before the first scan page load (integration with the fake source).
- [ ] Commit `feat: persisted mark index cache (FR-100, NFR-02)`.

---

### Task 10: Documents, publish, checklist (controller)

- [ ] Update `CLAUDE.md` project structure (Core/Marks, App EdgePanel/MarksListView, Pdf MarkPageSource, MarkIndexStore if built); update `docs/requirements.md` only where the build differs from FR-100..FR-104; publish to `publish-3b1` (a new folder if the old one is locked); give Fausto the Italian manual checklist: draw his usual vertical lines (long, short, wavy, tilted, double, left and right margins) and report missed/false marks; ticks on the rail; Alt+Up/Down and the buttons; marks list and its thumbnails; panel by finger swipe and by pen (pen at the left edge must ink); erase/undo of a line updates ticks; scrolling and pen unaffected while the scan runs on the 1,076-page book; opening time; memory; plus the still-open 3a checklist items. Wait for his results before calling it done.
