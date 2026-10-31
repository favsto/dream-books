# Phase 3a Multi-page Document Viewer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The whole PDF as one continuous, smooth vertical scroll (only nearby pages drawn), with document-wide zoom, go-to-page, a back button, a remembered position, a fading scroll rail with page preview (UI-07), and ink on every visible page, without regressing pen/finger/eraser, save, journal or recovery.

**Architecture:** Core gets pure, tested math and scheduling: `DocumentLayout` (page tops, visible range, anchored zoom, fit-to-widest), `PositionHistory` (back stack) and `RenderScheduler<TBitmap>` (priority, cancellation, byte-budget LRU). `DocumentSession` exposes an editor for any loaded page and a "reveal" request instead of forcing navigation. Pdf gets `ViewPositionStore` (per-document position file). App replaces the single-page `PageView` by a virtualised document view that creates a page surface (the existing `InkSurface`) only for the retained window and recycles it, feeds bitmaps from the scheduler, and draws the rail overlay.

**Tech Stack:** unchanged (.NET 10, WPF, PDFium via bblanchon.PDFium.Win32, CommunityToolkit.Mvvm, xUnit). No new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-10-31-phase3a-multipage-viewer-design.md` (read it first, including the "answers to the builder check" bullets in section 5).

## Global Constraints

- Core never references WPF/PDFium; PDFium calls only on the PDFium thread; the UI thread only handles input and drawing (NFR-04); strokes in page points, conversion only in the view layer (FR-26); nothing in this block changes how strokes are saved, verified or journalled beyond what the view state needs; the save pipeline, journal and recovery tests must stay green throughout.
- User-visible text only in `Strings.resx` (Italian) with the hand-written `Strings.Designer.cs` in sync (drift test); every new command has `AutomationProperties.Name`, 40 px touch targets, keyboard reachable; no hard-coded colours/durations outside `Theme.xaml`; fades respect the Windows animation setting (NFR-32).
- Nullable on; file-scoped namespaces; one public type per file named after the type; `_` prefix for private static readonly/const; warnings are errors; TDD in Core and Pdf; view models unit-tested; never run the WPF app; do not stage `.claude/settings.json`; commit trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`; `dotnet build` 0 warnings, `dotnet test` green, `dotnet format --verify-no-changes` clean at the end of every task (if Smart App Control blocks a freshly built DLL, rebuild once or run Release and say so).
- Constants (one place, `ViewerConstants` in App or Core as fits): retained window = visible pages ± 2 (NFR-05); bitmap cache budget 250 MB; page gap 8 pt; position stack 20; rail idle fade 1.5 s (180 ms in / 200 ms out, ease-out); preview ≈ 300 px wide; zoom 25%–800%; "fit width" = widest page of the document.
- Fausto's real PDFs: copies only, `C:\Users\Favsto\workspace\temp` (or `DREAMBOOKS_FIXTURES`), never printed names/text, skip cleanly when absent.

## Review Focus

1. **Nothing jumps**: zoom, window resize, page loads and bitmap arrivals never move the content under the user's fingers (anchored zoom within ±0.5 px; layout exact from the first frame; pages of different sizes). Pinned in Tasks 1, 7, 10.
2. **No blank pages, no stale bitmaps**: a page without a bitmap shows the placeholder; a bitmap for the wrong zoom/page/renderer is never shown as final; fast scroll shows the preview render first. Pinned in Tasks 3, 8.
3. **Memory/CPU stay flat while scrolling a 1,000-page book**: surfaces and bitmaps recycled, cache within budget, cancelled renders for pages that left the window. Pinned in Tasks 3, 7, 10.
4. **Ink and erase on the right page**: pen on a visible page that is not current inks there without changing the current page; erase belongs to the page it starts on; stroke crossing a page edge is clipped (FR-27); zoom lock while a gesture is live. Pinned in Tasks 5, 6, 7, 10.
5. **Save, journal, restore with several pages visible**: saving while pages are visible and while scrolling; the brief renderer swap of a plain Save never leaves blank pages (retry); restored position (journal wins over the position file) lands on the same spot, also on a different window size. Pinned in Tasks 6, 8, 10.

---

### Task 1: `DocumentLayout` — layout math

**Files:**
- Create: `src/DreamBooks.Core/Layout/DocumentLayout.cs`, `ViewPosition.cs`, `LayoutConstants.cs` (page gap)
- Test: `tests/DreamBooks.Core.Tests/Layout/DocumentLayoutTests.cs`

**Interfaces:**
- Produces (namespace `DreamBooks.Core.Layout`):
  - `readonly record struct ViewPosition(int PageIndex, double YPt, double ColumnXPt, double Zoom)` — `PageIndex` = page covering the viewport's top edge (when the edge is in the gap above page k, k with negative `YPt`); `YPt` = viewport top below that page's top, in page points; `ColumnXPt` = x of the viewport's horizontal centre in page points from the left edge of the document column (the widest page); `Zoom` as a number (1.0 = 100%).
  - `sealed class DocumentLayout(IReadOnlyList<PageDimensions> pages, double gapPt = 8)` with: `int PageCount`, `double ColumnWidthPt` (widest page), `double TotalHeightPt`, `double PageTopPt(int i)`, `double PageLeftPt(int i)` (centred in the column), `int PageAt(double yPt)` (clamped to valid pages; gap belongs to the page below), `(int First, int Last) VisibleRange(double topPt, double heightPt)` (inclusive, clamped), `(int First, int Last) RetainedRange(double topPt, double heightPt, int margin = 2)`, `double FitWidthZoom(double viewportWidthDip)` = `viewportWidthDip / (ColumnWidthPt × Units.DipPerPt)` clamped 0.25–8, `ViewPosition ToPosition(double offsetXDip, double offsetYDip, double viewportWidthDip, double zoom)`, `(double OffsetXDip, double OffsetYDip) ToOffsets(ViewPosition p, double viewportWidthDip, double viewportHeightDip)` (clamped to the scrollable extent), `ViewPosition ZoomAt(ViewPosition current, double newZoom, double anchorXDip, double anchorYDip, double viewportWidthDip)` keeping the document point under the anchor still, `double ExtentWidthDip(double zoom, double viewportWidthDip)` = `max(viewportWidthDip, ColumnWidthPt × zoom × DipPerPt)` and `double ExtentHeightDip(double zoom)`. All values double, no allocation per call in the hot ones (`PageAt`, `VisibleRange` use binary search over precomputed tops).

- [ ] **Step 1: Failing tests** (`Units.DipPerPt` = 96/72):

```csharp
[Fact] public void Tops_accumulate_page_heights_and_gaps()                      // 3 pages 600x800: tops 0, 808, 1616; TotalHeight 2424
[Fact] public void Narrower_pages_are_centred_in_the_widest_column()            // widths 600 and 400: PageLeftPt = 100 for the narrow one
[Fact] public void PageAt_maps_inside_gap_and_ends_correctly()                  // y in the gap above page k → k; y < 0 → 0; y > total → last
[Fact] public void VisibleRange_includes_partially_visible_pages_and_is_clamped()
[Fact] public void RetainedRange_extends_by_margin_and_clamps()
[Fact] public void FitWidthZoom_uses_the_widest_page_and_is_clamped()           // widest 600 pt, viewport 1000 dip → 1000/(600×96/72) = 1.25
[Fact] public void ToPosition_then_ToOffsets_round_trips_at_several_zooms()     // zooms 0.25, 1, 2.5, 8; random offsets (seeded) within extent; ±1e-6 dip
[Fact] public void ZoomAt_keeps_the_anchor_point_within_half_a_pixel()          // 50 random anchors/zooms, convert the anchor's document point before and after
[Fact] public void ZoomAt_works_at_the_document_start_and_end_and_with_a_narrow_viewport()
[Fact] public void Position_is_stable_across_viewport_width_changes_in_fit_mode()  // same ViewPosition at two widths → ToOffsets differ but YPt/PageIndex semantics stay
[Fact] public void Ten_thousand_pages_of_mixed_sizes_perform_in_microseconds()  // 10,000 PageAt/VisibleRange calls < 50 ms total
[Fact] public void Pages_with_zero_or_invalid_size_are_rejected()
```

- [ ] **Step 2:** `dotnet test tests/DreamBooks.Core.Tests` → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS; format.
- [ ] **Step 5:** Commit `feat: document layout math with anchored zoom and fit-to-widest (FR-05, FR-06, NFR-03)`.

---

### Task 2: `PositionHistory` — the back stack

**Files:** Create `src/DreamBooks.Core/Layout/PositionHistory.cs`; Test `tests/DreamBooks.Core.Tests/Layout/PositionHistoryTests.cs`.

**Interfaces:** Produces `sealed class PositionHistory(int capacity = 20)`: `void Push(ViewPosition p)` (ignores a position equal within tolerance to the top: same page and |ΔY| < 1 pt), `bool TryPop(out ViewPosition p)`, `bool CanGoBack`, `void Clear()`, `event EventHandler? Changed`; oldest dropped beyond capacity.

- [ ] **Step 1: Failing tests:** `Push_then_pop_returns_last_first`; `Duplicate_of_top_is_ignored`; `Capacity_drops_the_oldest`; `Pop_on_empty_returns_false`; `Changed_fires_on_push_pop_clear_only_when_state_changes`; `Clear_empties`.
- [ ] **Step 2–4:** red → implement → green; format. **Step 5:** Commit `feat: back-position history (FR-09)`.

---

### Task 3: `RenderScheduler<TBitmap>` — priority, cancellation, budgeted cache

**Files:** Create `src/DreamBooks.Core/Rendering/RenderScheduler.cs`, `IBitmapRenderer.cs`, `RenderRequest.cs`, `RenderQuality.cs`, `CachedBitmap.cs`; Test `tests/DreamBooks.Core.Tests/Rendering/RenderSchedulerTests.cs`.

**Interfaces (namespace `DreamBooks.Core.Rendering`):**
- `enum RenderQuality { Preview, Sharp }`.
- `interface IBitmapRenderer<TBitmap> { Task<TBitmap?> RenderAsync(int pageIndex, int pixelWidth, int pixelHeight, CancellationToken ct); }` (null = failed/not available, never throws for expected failures).
- `readonly record struct RenderRequest(int PageIndex, int Priority, int SharpWidth, int SharpHeight, int PreviewWidth, int PreviewHeight)` (lower `Priority` number = more urgent; 0 = visible).
- `sealed record CachedBitmap<TBitmap>(TBitmap Bitmap, RenderQuality Quality, int PixelWidth, int PixelHeight, long Generation)`.
- `sealed class RenderScheduler<TBitmap>(IBitmapRenderer<TBitmap> renderer, Func<TBitmap, long> sizeOf, long byteBudget, Func<SynchronizationContext?>? context = null)`: `void SetWanted(IReadOnlyList<RenderRequest> wanted)` (replaces the wanted set: pages no longer wanted are cancelled and become eviction candidates; new ones queued by priority; one render at a time unless the builder proves two are safe; per page: Preview first if nothing cached, then Sharp when `Sharp*` differs from the cached sharp size), `bool TryGet(int pageIndex, out CachedBitmap<TBitmap>? bitmap)`, `void InvalidateAll()` (zoom/renderer/DPI change: bumps a generation, keeps old bitmaps as stretchable previews flagged stale, re-queues), `void InvalidatePage(int pageIndex)`, `event EventHandler<RenderedEventArgs<TBitmap>>? Rendered` (page index, quality, generation; raised on the captured context), `long CachedBytes`, `int CachedCount`, `void Dispose()`. Eviction: LRU among pages NOT in the wanted set first; never evict a wanted page's sharp bitmap; if the wanted set alone exceeds the budget, keep wanted previews only. A render result for a stale generation or a page no longer wanted is discarded. A renderer returning null schedules ONE retry after `InvalidateAll()`/next `SetWanted` with the same request, not a tight loop.

- [ ] **Step 1: Failing tests** (fake renderer with gates/delays, fake sizeOf, deterministic):

```csharp
[Fact] public async Task Visible_pages_render_before_neighbours()
[Fact] public async Task Preview_arrives_before_sharp_for_a_page_without_a_bitmap()
[Fact] public async Task Page_that_leaves_the_wanted_set_is_cancelled_and_its_result_discarded()
[Fact] public async Task Stale_generation_results_are_discarded_after_InvalidateAll()
[Fact] public async Task InvalidateAll_keeps_old_bitmaps_available_flagged_stale_until_replaced()   // no blank page
[Fact] public async Task Cache_stays_within_the_byte_budget_evicting_unwanted_LRU_first()
[Fact] public async Task Wanted_sharp_bitmaps_are_never_evicted()
[Fact] public async Task Renderer_returning_null_is_retried_once_not_in_a_loop()
[Fact] public async Task A_thousand_scroll_updates_do_not_leak_tasks_or_bitmaps()                    // SetWanted 1000 times with a moving window over 1,000 pages; CachedCount ≤ budget/size; no unobserved exceptions
[Fact] public async Task Rendered_event_is_raised_on_the_captured_context()
[Fact] public async Task Dispose_cancels_everything_and_stops_events()
```

- [ ] **Step 2–4:** red → implement → green; format. **Step 5:** Commit `feat: render scheduler with priority, cancellation and budgeted cache (NFR-03, NFR-05, NFR-06, NFR-07)`.

---

### Task 4: `ViewPositionStore` — remembered position for saved documents

**Files:** Create `src/DreamBooks.Pdf/ViewPositionStore.cs`, `StoredPosition.cs`; Test `tests/DreamBooks.Pdf.Tests/ViewPositionStoreTests.cs`.

**Interfaces:** `StoredPosition(ViewPosition Position, DateTime SavedUtc)`; `sealed class ViewPositionStore(string rootDirectory, Func<DateTime>? clock = null)` (production root `%LOCALAPPDATA%\DreamBooks\positions`): `StoredPosition? TryLoad(string documentPath)`, `void Save(string documentPath, ViewPosition position)`, `void Delete(string documentPath)`, `void Prune(int maxEntries = 500, TimeSpan? maxAge = null)` (default 365 days). One file per document `<RecoveryStore.DocumentKey(path)>.json` with `{"v":1,"page":..,"y":..,"x":..,"zoom":..,"saved":"..."}`; atomic write (temp + replace); corrupt/unknown-version/NaN/invalid values → `TryLoad` returns null (never throws); `Save` never throws for I/O errors (returns silently after reporting through an `event EventHandler<Exception>? Failed` carrying no document content); ASCII-only; paths never inside the PDF's folder.

- [ ] **Step 1: Failing tests:** `Save_then_TryLoad_round_trips_every_field`; `Different_paths_do_not_collide_and_case_is_ignored`; `Corrupt_file_returns_null`; `Unknown_version_returns_null`; `NaN_or_negative_zoom_returns_null`; `Missing_folder_returns_null_and_Save_creates_it`; `Prune_removes_old_and_beyond_max_entries_oldest_first`; `Save_is_atomic_a_reader_never_sees_a_torn_file` (parallel readers while writing 500 times); `Save_failure_raises_Failed_and_does_not_throw` (read-only folder); `Files_contain_no_document_path_or_text`.
- [ ] **Step 2–4:** red → implement → green; format. **Step 5:** Commit `feat: per-document view position store (FR-04)`.

---

### Task 5: `DocumentSession` for several visible pages

**Files:** Modify `src/DreamBooks.Core/Documents/DocumentSession.cs` (and only what it needs); Test `tests/DreamBooks.Core.Tests/Documents/DocumentSessionMultiPageTests.cs`, existing session/undo tests stay green.

**Interfaces (additions; keep existing members working):**
- `Task<PageEditor> EnsureEditorAsync(int pageIndex, CancellationToken ct = default)` — loads the page's strokes once (existing lazy load) and returns its cached `PageEditor` (one per page, sharing `Undo`); `bool TryGetEditor(int pageIndex, out PageEditor? editor)` for already loaded pages.
- `void SetCurrentPageFromView(int pageIndex)` — sets `CurrentPageIndex` (raises `CurrentPageChanged` only when it changes) WITHOUT loading strokes and without touching undo; ignored after Dispose; out-of-range throws `ArgumentOutOfRangeException`.
- `event EventHandler<RevealRequestedEventArgs>? RevealRequested` (`PageIndex`): `UndoAsync/RedoAsync` raise it for the command's page instead of calling `GoToPageAsync` (they still `EnsureEditorAsync` for that page so the command can run); the view decides whether to scroll (only if the page is not visible). `GoToPageAsync` keeps its meaning for explicit "go to" (loads, sets current, raises `CurrentPageChanged`).
- `PageEditor`s created by `GoToPageAsync` and by `EnsureEditorAsync` are the SAME instances per page.

- [ ] **Step 1: Failing tests:** `EnsureEditorAsync_returns_the_same_editor_per_page_and_loads_strokes_once`; `Concurrent_EnsureEditorAsync_calls_share_one_load`; `Editors_of_several_pages_share_one_undo_history_and_dirty_state`; `SetCurrentPageFromView_changes_current_without_loading_or_clearing_undo`; `SetCurrentPageFromView_raises_only_on_change`; `Undo_of_a_command_on_another_page_raises_RevealRequested_and_applies_the_undo`; `Undo_does_not_call_GoToPage_anymore_but_GoToPageAsync_still_works`; `Dirty_and_GetChanges_include_pages_edited_through_EnsureEditorAsync`; `Disposed_session_ignores_SetCurrentPageFromView`; all existing session tests pass unchanged (adapt only where undo navigation semantics are tested: assert `RevealRequested` instead of `CurrentPageChanged`).
- [ ] **Step 2–4:** red → implement → green; full Core suite; format. **Step 5:** Commit `feat: editors for any loaded page and reveal requests instead of forced navigation (FR-26, FR-62)`.

---

### Task 6: View models for the document view

**Files:** Modify `src/DreamBooks.App/ViewModels/MainViewModel.cs`, `ZoomViewModel.cs`; Create `src/DreamBooks.App/ViewModels/DocumentViewportViewModel.cs`; Test `tests/DreamBooks.App.Tests/DocumentViewportViewModelTests.cs`, adapt `MainViewModelTests`, `SaveCommandsTests`, `RecoveryServiceTests` only where the single-page assumptions changed.

**Interfaces (decided here):**
- `DocumentViewportViewModel` (owned by `MainViewModel`, `public DocumentViewportViewModel Viewport`): `DocumentLayout? Layout` (built from `session.GetPageDimensions` for all pages at open), `ViewPosition Position`, `double ViewportWidthDip/ViewportHeightDip` (set by the view), `bool IsFitWidth`, `IReadOnlyList<int> VisiblePages`, `(int First,int Last) RetainedRange`, `int CurrentPage` (page covering the viewport centre; pushes `session.SetCurrentPageFromView` debounced 150 ms through an injectable scheduler/timer), `string IndicatorText` ("12 / 48 · 125%"), `void SetScroll(double offsetXDip, double offsetYDip)`, `void ZoomAt(double factor, Point anchorDip)` honouring `ZoomVm.IsLocked`, `void ToggleFitWidth(double? anchorYDip)`, `void ScrollToPage(int pageIndex, bool recordHistory)` (pushes the previous position on `History` when `recordHistory`), `PositionHistory History`, `IRelayCommand BackCommand`, `IAsyncRelayCommand<int> GoToPageCommand` (1-based number; clamps; records history), `PageUp/PageDown/Home/End` commands, `event EventHandler? PositionChanged`, `event EventHandler? LayoutChanged`.
- `MainViewModel`: `PageNumber`/`PageSize`/`Editor` keep meaning "current page"; per-page erase: `void BeginErase(PageEditor editor)` (replaces the parameterless one; no-op if a gesture is live), `PageEditor? ActiveErasePage`, `EraserSession? ActiveErase` (as now), `EraseAt/EndErase/CancelErase` unchanged otherwise; `ErasePreviewChanged` unchanged (InkSurface filters by its own editor); `Task<PageEditor> EnsureEditorAsync(int pageIndex)` forwarding to the session; handling of `session.RevealRequested`: scroll only if the page is NOT in `VisiblePages`; navigation commands (GoToPage, Previous/Next, Back, rail) are NOT disabled while `IsSaving` any more; position persistence: on open, load `ViewPositionStore` position unless a journal view-state exists (journal wins), apply it clamped; save the position when the view settles (500 ms, same debounce as the journal view state) and on close/swap; the journal `RecordView(pageIndex, zoom, scrollX, scrollY)` now receives (page of the top edge, zoom, column X in page points, Y offset inside the page in page points) — meaning of the stored numbers changes, the format does not; old journals still restore (interpret old DIP offsets as best effort: clamp).
- `ZoomViewModel`: keeps `Zoom` (25–800%), steps, `IsLocked`; adds `bool IsFitWidth` (turned off by any manual zoom or step; turned on by `DocumentViewportViewModel.ToggleFitWidth`); `FitWidth(viewportWidthDip, pageWidthPt)` stays for compatibility with the widest page width.
- Existing tests stay valid; replace the single-page `PageChanged` semantics where needed but keep the event (raised when the CURRENT page changes).

- [ ] **Step 1: Failing tests:** `Layout_is_built_from_all_page_dimensions_at_open_without_loading_strokes`; `CurrentPage_follows_the_viewport_centre_debounced_and_does_not_load_strokes`; `GoToPage_clamps_records_history_and_BackCommand_returns`; `Rail_drag_start_records_history_once`; `ZoomAt_is_ignored_while_ZoomVm_is_locked_and_keeps_the_anchor`; `FitWidth_uses_the_widest_page_and_resize_recomputes_only_in_fit_mode`; `Manual_zoom_turns_fit_mode_off_and_DoubleTap_toggles_it`; `Pen_on_a_visible_non_current_page_can_erase_without_changing_current_page` (BeginErase(editor) for page 3 while current is 1); `Erase_on_one_page_blocks_a_second_gesture_until_it_ends`; `Undo_of_a_stroke_on_a_visible_page_does_not_scroll`; `Undo_of_a_stroke_on_a_hidden_page_scrolls_to_reveal_it`; `Navigation_commands_stay_enabled_while_saving`; `Position_restore_prefers_the_journal_view_over_the_position_store`; `Position_is_saved_when_the_view_settles_and_on_close`; `Journal_view_entry_uses_page_relative_offsets`; `No_position_file_is_created_for_a_document_that_was_never_opened_successfully`; existing MainViewModel/Save/Recovery tests pass.
- [ ] **Step 2–4:** red → implement → green; full suite; format. **Step 5:** Commit `feat: viewport view model with history, fit-width mode, per-page erase and persisted position (FR-04, FR-06, FR-09, FR-26)`.

---

### Task 7: Virtualised document view with a surface per visible page

**Files:** Create `src/DreamBooks.App/Views/DocumentView.xaml(.cs)`, `src/DreamBooks.App/Views/PageSurfacePool.cs`, `src/DreamBooks.App/Views/VirtualPagePanel.cs`; Modify `src/DreamBooks.App/Ink/InkSurface.cs` (give it its own `PageEditor` instead of comparing with `ViewModel.Editor`: add `public static readonly DependencyProperty EditorProperty`; `BeginEraseGesture`, `CanInk`-style checks, `FinishPenStroke` and `RefreshInk` use `Editor` of THIS surface and `ViewModel.ActiveErasePage` for the eraser preview; `ViewModel` stays for tools/zoom/erase API), `MainWindow.xaml` (host `DocumentView` instead of `PageView`), delete the obsolete single-page `PageView.xaml(.cs)` only when nothing references it (move its reusable logic: manipulation, Ctrl+wheel, zoom lock, view-state debounce); Test `tests/DreamBooks.App.Tests/DocumentViewTests.cs`, extend `ViewSmokeTests.cs`.

**Interfaces/behaviour:**
- `DocumentView : UserControl` bound to `MainViewModel`: one `ScrollViewer` (finger manipulation: translate + scale, `PanningMode=None`, Ctrl+wheel, double tap (one finger, 300 ms, within 24 px) toggling fit width/200% around the tap; boundary feedback off; touch never inks) containing a `VirtualPagePanel` whose size is the document extent from `DocumentLayout` and whose children are ONLY the surfaces for `RetainedRange`; each child is an `InkSurface` (existing control: bitmap layer, retained ink layer, wet renderer, eraser cursor) positioned at its layout rect (DIPs = pt × zoom × 96/72, centred in the column); surfaces are taken from `PageSurfacePool` (max retained + 2 spare), re-bound (`Page`, `Editor`, `Scale`, `PageImage = null`), and returned when the page leaves the window (any live gesture on a recycled surface is ended first; recycling a surface that `IsInking` is deferred until pen-up).
- Pages entering the window call `vm.EnsureEditorAsync(index)`; until it completes the surface is created WITHOUT an editor (placeholder only, does not accept ink: pen/eraser gating requires `Editor != null`); when it completes the surface binds `Editor` and displays strokes (works while a save occupies the PDFium thread: placeholders stay, ink on loaded pages keeps working).
- Placeholder: a white page of the exact page size with a very light fade-in (instant under reduced animations); never a blank hole.
- Scroll and zoom drive `vm.Viewport.SetScroll/ZoomAt`; layout offsets come from `DocumentLayout`; zoom is anchored with `ZoomAt`; resize updates `ViewportWidthDip/HeightDip` (fit mode recomputes); DPI changes invalidate bitmaps (Task 8 hook: raise `vm.Viewport.LayoutChanged`/an `InvalidateBitmaps` event, a no-op until Task 8). The zoom lock while a stroke or erase is live is preserved; manipulation is ignored while any visible surface `IsInking`.
- Current page = centre page: reported through the viewport VM (debounced); pen-down never touches it.
- Position restore: `vm.ViewToRestore`/stored position applied once after the first layout (page, YPt, ColumnXPt → offsets, clamped).
- Keep the existing STA test seams for `InkSurface` working (`Surface` accessors used by tests: adapt through the pool).

- [ ] **Step 1: Failing tests (STA, 30-page generated PDF through the real VM unless noted):**

```csharp
[Fact] public void Only_the_retained_window_of_pages_has_surfaces()                           // visible ±2 → count ≤ visible+4; other pages absent from the visual tree
[Fact] public void Scrolling_recycles_surfaces_and_total_created_stays_bounded()                // scroll through all 30 pages programmatically: created ≤ window + spare
[Fact] public void Surfaces_are_positioned_at_the_layout_rects_at_several_zooms()
[Fact] public void Placeholder_is_shown_for_pages_without_a_bitmap()
[Fact] public void Pen_stroke_on_a_visible_page_that_is_not_current_commits_there_and_keeps_current_page()    // use InkSurface test hooks BeginPenStroke/FinishPenStroke on page 3's surface
[Fact] public void Erase_gesture_starts_on_the_page_under_the_eraser_only()
[Fact] public void Stroke_that_starts_on_one_page_and_leaves_it_is_clipped_to_that_page()                     // FR-27
[Fact] public void Surface_without_an_editor_does_not_accept_ink_until_the_editor_arrives()
[Fact] public void Recycling_a_surface_that_is_inking_is_deferred()
[Fact] public void Zoom_keeps_the_page_point_under_the_anchor_still_in_the_real_scroll_viewer()               // ±0.5 px
[Fact] public void Double_tap_toggles_fit_width_and_200_percent_around_the_tap()
[Fact] public void Resize_in_fit_mode_recomputes_zoom_and_keeps_the_top_position()
[Fact] public void Restored_position_is_applied_once_after_first_layout()
[Fact] public void MainWindow_still_instantiates_with_the_new_view()                                          // STA smoke
```

- [ ] **Step 2–4:** red → implement → green; full suite; format. **Step 5:** Commit `feat: virtualised multi-page document view with a recycled ink surface per page (FR-05, FR-06, FR-07, FR-26, FR-27, NFR-03, NFR-05)`.

---

### Task 8: Bitmaps through the scheduler (preview first, sharp after, retry, DPI)

**Files:** Create `src/DreamBooks.App/Views/PageBitmapRenderer.cs` (`IBitmapRenderer<BitmapSource>` over the swappable renderer, `RenderSizePolicy` for sharp sizes, a ~300 px-wide preview size, BGRA → frozen `BitmapSource` via the existing `PageBitmap.FromRendered`), `src/DreamBooks.App/Views/PageBitmapCoordinator.cs`; Modify `DocumentView.xaml.cs`, `MainViewModel`/`SwappableRenderer` events as needed; Test `tests/DreamBooks.App.Tests/PageBitmapCoordinatorTests.cs`.

**Interfaces/behaviour:** `PageBitmapCoordinator` owns a `RenderScheduler<BitmapSource>` (budget 250 MB, `sizeOf` = pixel bytes), turns (`RetainedRange`, visible range, zoom, DPI scale) into `SetWanted` requests (visible: priority 0; neighbours by distance), pushes arriving bitmaps to the surface of that page (`PageImage`; a stale-flagged bitmap is shown stretched until the sharp one arrives; never `null` once something was shown for that page), calls `InvalidateAll()` on zoom settle (debounce 150 ms, as before), DPI change and renderer swap (`MainViewModel.Renderer` change after a Save: failed renders retry automatically), cancels everything on dispose/document change; the preview ~300 px wide is also used by the rail bubble (Task 9) through `Task<BitmapSource?> GetPreviewAsync(int pageIndex, ct)`; no PDFium call on the UI thread; render failures never throw to the UI (NFR-11).

- [ ] **Step 1: Failing tests** (fake `IBitmapRenderer`, STA only where a surface is needed): `Visible_pages_are_requested_before_neighbours_and_far_pages_not_at_all`; `Fast_scroll_shows_preview_first_then_sharp`; `Zoom_settle_invalidates_and_keeps_old_bitmaps_stretched_until_replaced`; `Dpi_change_invalidates`; `Renderer_swap_after_save_retries_failed_pages`; `Pages_that_leave_the_window_are_cancelled`; `Cache_stays_within_budget_while_scrolling_300_pages`; `Failure_never_throws_and_leaves_the_placeholder`; `Preview_for_the_rail_is_small_and_cached`; `No_render_is_requested_on_the_UI_thread` (renderer fake asserts thread).
- [ ] **Step 2–4:** red → implement → green; full suite; format. **Step 5:** Commit `feat: page bitmaps through the render scheduler with preview-first, retry and budgeted cache (NFR-03, NFR-05, NFR-07, NFR-22)`.

---

### Task 9: The scroll rail, go-to-page, keyboard and back button

**Files:** Create `src/DreamBooks.App/Views/ScrollRail.xaml(.cs)`; Modify `DocumentView.xaml(.cs)`, `MainWindow.xaml` (panel: go-to-page field + back button, KeyBindings), `Strings.resx` + `Strings.Designer.cs`, `Theme.xaml` (durations/colours as resources); Test `tests/DreamBooks.App.Tests/ScrollRailTests.cs`, `StringsTests` drift.

**Interfaces/behaviour (UI-07):** `ScrollRail` overlay on the right edge (40 px touch target, 8 px visible bar, knob 34 px) bound to `vm.Viewport` (position fraction = scroll offset / extent): appears on scroll, mouse-wheel, or touch/hover near the edge; fades out after 1.5 s idle (state machine pure and testable: `RailVisibilityState` with an injectable clock: Hidden → FadingIn 180 ms → Visible → FadingOut 200 ms, ease-out; `ReducedMotion` = instant); dragging the knob scrubs through the document (`vm.Viewport.ScrollToPage`-style continuous offset; `History.Push` once at drag start) and shows ONE preview bubble beside the knob with the page number and `PageBitmapCoordinator.GetPreviewAsync` image (placeholder until it arrives); tapping the track jumps there; the indicator "12 / 48 · 125%" (bottom right, fades with the rail) opens a numeric field on tap (`GoToPageCommand`, Enter confirms, Esc cancels, invalid input ignored); keyboard: PgUp/PgDn/Home/End on the document view (not while typing in the field), Alt+Left = Back; panel: Back button (disabled when `!History.CanGoBack`) and a go-to-page field; Italian strings via resx: `RailLabel`, `RailPreviewLabel`, `GoToPage`, `GoToPageHint`, `Back`, `PageIndicatorLabel`, with AutomationProperties.Name; Windows "animation effects off" respected through `SystemParameters.ClientAreaAnimation`; the rail never intercepts pen/ink input outside its 40 px strip; no marks yet (3b).

- [ ] **Step 1: Failing tests:** `Rail_state_machine_fades_in_visible_and_out_after_idle_with_the_stated_durations`; `Reduced_motion_makes_changes_instant`; `Scroll_activity_resets_the_idle_timer`; `Drag_scrubs_continuously_and_pushes_history_once`; `Track_tap_jumps_to_that_position`; `Preview_bubble_shows_the_page_number_and_requests_a_preview`; `Indicator_text_formats_page_count_and_zoom`; `Go_to_page_field_accepts_valid_input_and_ignores_invalid`; `PgUp_PgDn_Home_End_move_by_viewport_page_and_to_the_ends`; `Alt_Left_goes_back`; `Rail_does_not_block_pen_input_outside_its_strip` (hit-test); `Strings_resx_and_Designer_stay_in_sync_with_the_new_keys` (existing drift test); STA smoke: the window instantiates and the rail, back button and field exist.
- [ ] **Step 2–4:** red → implement → green; full suite; format. **Step 5:** Commit `feat: fading scroll rail with page preview, go to page, keyboard and back button (UI-07, FR-09, FR-10, NFR-32)`.

---

### Task 10: Integration, large documents and regression sweep

**Files:** Test `tests/DreamBooks.App.Tests/MultiPageIntegrationTests.cs`; fixes anywhere the tests expose problems (TDD).

**Behaviour to pin (real VM + generated PDFs with `TestPdfFactory`, plus real-copy tests with skip):**
- Open a generated 300-page PDF: first page visible quickly (the viewport VM has layout and the first surfaces after the first `LayoutUpdated`; assert < 1 s wall clock on a generated file, generously), surfaces ≤ window + spare after scrolling the whole document, bitmap cache within budget, no unobserved task exceptions.
- Real e-book copy (1,076 pages; skip if absent): open + layout + scroll through all pages programmatically: surfaces bounded, cache bounded, process memory delta < 400 MB (GC + `Process.PrivateMemorySize64` before/after, generous), open time reported (sizes/times only).
- Draw strokes on three different visible pages, Save (real saver), reopen the saved file through the VM: strokes on all three pages; crash simulation (new VM over the same journal folder): strokes and position restored, position equals the journal view (page-relative) also with a different `ViewportWidthDip`.
- Save while scrolling and with several pages visible: the swap window never leaves a page without a bitmap after the swap (placeholder/old bitmap, then sharp); navigation commands work during the save; pages entering during the save do not accept ink until their editor is ready.
- Erase on page 2 then undo from page 5 (hidden): reveal scrolls to page 2; undo for a visible page does not scroll.
- Regression: pen/finger/eraser gating tests, zoom lock, undo/redo, journal, recovery and save pipeline suites stay green; `dotnet format` clean.
- Fixes found by these tests are made test-first in the owning layer.

- [ ] **Step 1:** write the tests above; **Step 2:** run, fix failures test-first; **Step 3:** full suite + format; **Step 4:** Commit `test: multi-page integration, large documents and restore sweep (NFR-02, NFR-05, NFR-06)`.

---

### Task 11: Documents, publish, checklist (controller)

- [ ] Update `CLAUDE.md` project structure (Core/Layout, Core/Rendering, Pdf position store, App DocumentView/ScrollRail); `docs/superpowers/specs` stays; publish to a NEW folder (`publish-3a`) when the old one is locked; measure open/scroll memory with the real e-book copy; give Fausto the Italian manual checklist (smoothness, no white pages, zoom keeps position, rail fade and preview, go to page, back, reopen at the same place, pen/finger/back end unchanged, memory) and wait.
