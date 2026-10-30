# Phase 1 follow-up: adopt the custom ink renderer, make it fast

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep only the custom stroke renderer (mode B), default to the firm pressure curve and strong smoothing as Fausto chose on the device, and remove the lag he saw with many strokes on a page.

**Architecture:** Mode A (stock WPF ink) is deleted. Committed strokes are drawn from one closed polygon per stroke (instead of an overlapping union of ~400 discs and quads), each stroke is its own retained visual so a commit or an erase touches only that stroke, and zoom changes a transform only.

**Tech Stack:** unchanged (.NET 10, WPF, xUnit).

**Spec:** `docs/superpowers/specs/2026-10-30-phase1-ink-prototype-design.md` (decision update below); implements the follow-up agreed with Fausto on 2026-10-30.

## Why (measured, not yet profiled on the Surface)

Synthetic handwriting, firm curve, strong smoothing: a 100 pt stroke is ~400 figures / ~2,700 vertices; 1,500 such strokes are ~600,000 figures / ~4 million vertices. Building them is cheap (0.3 ms per stroke); drawing them every frame while panning or zooming is what lags. `CustomInkLayer.OnRender` also re-records every stroke on each commit.

## Global Constraints

- Everything from the phase 1 plan still applies (stroke coordinates in page points; every model change through `IUndoableCommand`; Core free of WPF/PDFium; user-visible text only in `Strings.resx` (Italian); nullable on; file-scoped namespaces; one public type per file; warnings are errors; TDD in Core; never run the app; do not stage `.claude/settings.json`; commit trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`).
- Pressure → width stays `BaseWidth × (0.3 + 1.2 × curve(p))` (FR-22). The final stroke must look the same as today to the eye: same width profile, no visible shift of the path, small details (dots, sharp corners) preserved.
- The outline function is reused by phase 2 for the PDF appearance stream, so it must stay pure Core math returning closed polygons in page points with positive winding (y down).
- Defaults chosen by Fausto: pressure curve **Decisa** (`PressureCurve.Firm`), smoothing **Forte** (`SmoothingLevel.Strong`).

## Review Focus

1. Tight loops and 180° reversals: the offset curves may fold over; the stroke must still cover its whole centerline under the non-zero rule (no holes).
2. A tap (one point), two identical points, and a stroke shorter than its own width must render as a visible dot of the right diameter.
3. Width jumps (pressure 0 → 1 between neighbours) must not leave gaps or spikes.
4. Erase preview and undo after an erase must keep the visuals consistent (same stroke instances restored, no stale visuals, no double-draw).
5. Back-end and touch input must still never produce wet pen ink after mode A's thread filter is merged into the remaining renderer.

---

### Task 1: Remove mode A, record the decision

**Files:**
- Delete: `src/DreamBooks.App/ViewModels/RendererMode.cs`, `src/DreamBooks.App/Ink/StockStrokeConverter.cs`, `tests/DreamBooks.App.Tests/StockStrokeConverterTests.cs`
- Modify: `src/DreamBooks.App/Ink/InkSurface.cs`, `CustomDynamicRenderer.cs`, `PenDynamicRenderer.cs`, `CustomInkLayer.cs` (only as needed), `src/DreamBooks.App/ViewModels/ToolsViewModel.cs`, `src/DreamBooks.App/MainWindow.xaml`, `src/DreamBooks.App/Resources/Strings.resx` and `Strings.Designer.cs`, `tests/DreamBooks.App.Tests/ToolsViewModelTests.cs`, `ViewSmokeTests.cs`, `docs/requirements.md`

**Interfaces:**
- Produces: `ToolsViewModel` without `Renderer` and `IsCustomRenderer`; `Curve` defaults to `PressureCurve.Firm`, `Smoothing` to `SmoothingLevel.Strong`. `InkSurface` has no `Mode` property and no stock `InkPresenter`, no stock stroke collection, no reference-scale transform. The single wet renderer keeps refusing touch and pen back-end input on the stylus thread (the filter that `PenDynamicRenderer` provides today must survive: either `CustomDynamicRenderer` derives from it or absorbs it; delete whatever becomes dead).

- [ ] **Step 1: Failing tests** — update the tests first so they describe the new state: `ToolsViewModelTests` (defaults are Firm and Strong; no renderer property), `ViewSmokeTests` (delete the mode-A tests; keep every mode-B behaviour test: commit adds one stroke, zoom changes only the scale, undo after erase restores the same instances, renderer enabled/disabled gating for eraser and inverted pen, cursor tests; add one test that the single remaining wet renderer is installed exactly once and that touch/back-end devices are on its ignore list as before).
- [ ] **Step 2:** `dotnet test` → FAIL (compile or assertion).
- [ ] **Step 3:** Delete mode A code and the renderer selector from the panel; remove `Renderer`, `RendererStock`, `RendererCustom` string keys from resx **and** Designer (the resx↔Designer drift test must stay green); curve/smoothing controls are always enabled; defaults as above.
- [ ] **Step 4:** In `docs/requirements.md` (Italian) add a short note under the pen table in section 6 recording the decision: custom renderer adopted after on-device comparison with stock WPF ink (2026-10-30), default pressure curve "decisa" (FR-21) and smoothing "forte" (FR-23). Do not change any requirement ID.
- [ ] **Step 5:** `dotnet build` (0 warnings), `dotnet test`, `dotnet format --verify-no-changes` → all clean.
- [ ] **Step 6:** Commit `refactor: keep only the custom ink renderer, default firm curve and strong smoothing (FR-20, FR-21, FR-23)`.

---

### Task 2: One closed polygon per stroke

**Files:**
- Modify: `src/DreamBooks.Core/Ink/StrokeOutline.cs` (add the new builder, keep `Build`, which the wet renderer still uses), `src/DreamBooks.Core/Ink/StrokeGeometry.cs`
- Test: `tests/DreamBooks.Core.Tests/Ink/StrokeContourTests.cs` (new), existing `StrokeOutlineTests.cs` stays green

**Interfaces:**
- Consumes: `StrokeSmoother.Smooth`, `PressureMapping.WidthAt`, `PointD`.
- Produces: `StrokeOutline.BuildContour(IReadOnlyList<PointD> centers, IReadOnlyList<double> widths, double tolerancePt = 0.05) : IReadOnlyList<IReadOnlyList<PointD>>` returning **one closed figure** (a second figure only if you can justify it) with positive shoelace area. `StrokeGeometry.BuildOutline(InkStroke, PressureCurve, SmoothingLevel)` keeps its signature and now returns the single-contour result.

Algorithm (decided here; the builder chooses details inside it): offset the centerline left and right by `width/2` along per-point normals (direction = average of the adjacent segment directions); round caps at both ends and **round joins on the outer side** where the turning angle exceeds ~20° (arc vertex spacing so the sagitta is ≤ 0.02 pt); the inner side is offset straight and may fold over itself (non-zero fill covers it); then simplify the polygon with a tolerance of `tolerancePt` (Douglas–Peucker or equivalent) but never inside the caps by more than that tolerance. A single point, or points all within 0.01 pt, produces a disc of the right diameter.

- [ ] **Step 1: Failing tests** (names and assertions):

```csharp
[Fact] public void Single_point_gives_one_disc_of_the_given_width()           // bounds 2r ±0.03, area ≈ πr² within 1%
[Fact] public void Identical_points_give_a_disc()
[Fact] public void Output_is_one_closed_figure_with_positive_winding()
[Fact] public void Output_contains_no_NaN_or_infinity()                      // incl. width 0.01 and duplicate centers
[Fact] public void Straight_constant_width_line_has_expected_bounds()        // (0,0)-(10,0) width 2 → x∈[-1,11], y∈[-1,1] ±0.03
[Fact] public void Covers_every_centerline_point_under_nonzero_rule()        // tight loop (radius < width/2) and a 180° reversal: every center is inside
[Fact] public void Matches_the_union_outline_within_one_percent_of_area()    // typical wavy 100 pt stroke with varying pressure: |area(contour) − area(union of Build)| / area ≤ 1% (union area via fine point-in-polygon grid sampling)
[Fact] public void Sample_grid_agrees_with_the_union_outline_almost_everywhere() // grid every 0.1 pt over the bounds: mismatch ≤ 1% of covered samples
[Fact] public void Width_jump_between_neighbours_leaves_no_gap_or_spike()    // pressure 0 → 1 over one step; both discs' extreme points inside; max extent ≤ larger radius + step length
[Fact] public void Vertex_count_is_a_small_fraction_of_the_union_outline()   // 100 pt wavy stroke: vertices ≤ 400 and ≤ 15% of Build's vertex count; also assert it as an explicit budget so a regression fails
[Fact] public void Simplification_keeps_sharp_corner()                       // L-shape: the corner of the outer offset is within tolerance
[Fact] public void Pressure_changes_outline_width()                          // via StrokeGeometry: bounds height ratio 0.3/1.5 ±5%
```

- [ ] **Step 2:** `dotnet test tests/DreamBooks.Core.Tests` → FAIL.
- [ ] **Step 3:** Implement `StrokeOutline.BuildContour` and route `StrokeGeometry.BuildOutline` through it; keep `StrokeOutline.Build` untouched for the wet renderer.
- [ ] **Step 4:** Run → PASS; `dotnet build` 0 warnings; `dotnet format --verify-no-changes`.
- [ ] **Step 5:** Commit `perf: one closed polygon per stroke instead of a union of discs (FR-20, FR-22, FR-23)`.

---

### Task 3: One retained visual per stroke

**Files:**
- Modify: `src/DreamBooks.App/Ink/CustomInkLayer.cs`, `src/DreamBooks.App/Ink/InkSurface.cs` (only the parts that talk to the layer)
- Test: `tests/DreamBooks.App.Tests/ViewSmokeTests.cs`, `CustomInkLayerTests.cs` (new)

**Interfaces:**
- Consumes: `StrokeGeometry.BuildOutline`, `OutlineGeometry.ToStreamGeometry`.
- Produces: `CustomInkLayer` keeps its public surface (`Strokes`, `Scale`, `Curve`, `Smoothing`, `internal CachedCount`) but renders through a `VisualCollection` of one `DrawingVisual` per stroke (override `VisualChildrenCount` / `GetVisualChild`, hit-testing off), diffed against `Strokes` by stroke Id: a commit adds exactly one visual, an erase removes or replaces only the affected ones, z-order is preserved, restoring the same stroke instance after undo reuses or cheaply rebuilds its visual. Zoom changes only one shared `ScaleTransform` (the layer's `RenderTransform` or an equivalent single transform), never a visual. A curve or smoothing change rebuilds all visuals. No `InvalidateVisual` on the whole layer for a single-stroke change. Do **not** add `BitmapCache` (phase 3, needs tiling at high zoom).

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void Adding_a_stroke_adds_exactly_one_visual_and_keeps_the_others_by_reference()
[Fact] public void Removing_a_stroke_removes_only_its_visual()
[Fact] public void Restored_stroke_after_undo_appears_in_its_original_z_position()
[Fact] public void Zoom_changes_only_the_transform_and_keeps_every_visual_instance()
[Fact] public void Curve_or_smoothing_change_rebuilds_all_visuals()
[Fact] public void Visual_order_matches_stroke_order_after_mixed_add_and_remove()   // seeded Random(42), 50 operations
[Fact] public void Erase_preview_swap_of_DisplayStrokes_updates_only_changed_visuals()
```

- [ ] **Step 2:** `dotnet test tests/DreamBooks.App.Tests` → FAIL.
- [ ] **Step 3:** Implement. Geometry caching by stroke Id stays (a stroke is never mutated under the same Id).
- [ ] **Step 4:** Run → PASS; `dotnet build` 0 warnings; `dotnet format --verify-no-changes`.
- [ ] **Step 5:** Commit `perf: retained visual per stroke, commits and erases no longer redraw the page (NFR-04, NFR-06)`.

---

### Task 4: Publish and hand off

- [ ] **Step 1:** `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` all clean; then `dotnet publish src/DreamBooks.App -c Release -r win-x64 --self-contained`; confirm `DreamBooks.App.exe` and `pdfium.dll` in the publish folder (controller does this).
- [ ] **Step 2:** Controller re-runs the synthetic measurement (1,500 strokes × 100 pt) and reports figure and vertex counts before/after.
- [ ] **Step 3:** Controller gives Fausto the manual checklist (feel unchanged; a page with many lines pans and zooms smoothly) and waits.
