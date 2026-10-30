# Phase 1 Ink Prototype Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A WPF prototype that shows one PDF page and lets Fausto write on it
with pressure, erase with the pen's back end, undo/redo, zoom, and switch
live between stock WPF ink (mode A) and our custom renderer (mode B).

**Architecture:** `DreamBooks.Core` owns the model (strokes in page points),
undo/redo commands, and all ink math (pressure curves, smoothing, outline
geometry, clipping, eraser geometry), fully unit-tested. `DreamBooks.Pdf`
wraps PDFium through a thin P/Invoke layer on one dedicated worker thread.
`DreamBooks.App` hosts a page element that takes pen input through the WPF
real-time stylus (`DynamicRenderer`) and renders committed strokes either
with WPF `Stroke` rendering (A) or as filled outlines from Core (B).

**Tech Stack:** .NET 10, C#, WPF, CommunityToolkit.Mvvm 8.4.2,
bblanchon.PDFium.Win32 158.0.8125, xUnit (template default) + Microsoft.NET.Test.Sdk.

**Spec:** `docs/superpowers/specs/2026-10-30-phase1-ink-prototype-design.md`

## Global Constraints

- Target `net10.0` for Core, Pdf and their tests; `net10.0-windows` with `UseWPF` for App and App.Tests.
- Nullable enabled, implicit usings, file-scoped namespaces, one public type per file named after the type, `Async` suffix + `CancellationToken` on I/O (rules in `.claude/rules/dotnet.md`, now confirmed).
- `DreamBooks.Core` references no WPF, PDFium or PDFsharp. Only `DreamBooks.Pdf` touches PDFium.
- Only these NuGet packages (approved with the plan): `bblanchon.PDFium.Win32`, `CommunityToolkit.Mvvm`, and the xUnit test template packages. Nothing else without asking Fausto.
- Stroke coordinates in **page points (1/72 inch), origin top-left of the page as PDFium renders it, y pointing down**. Conversion to PDF user space (y up) is phase 2's job.
- Every model change goes through `IUndoableCommand` executed by `UndoManager`. The view never mutates `Page`.
- PDFium calls only on the PDFium worker thread; never on the UI thread (NFR-04).
- Pressure → width (mode B): `width = BaseWidth × (0.3 + 1.2 × curve(p))` (FR-22). Base width 0.25–5 mm, default 0.5 mm. Undo history 500.
- User-visible text is Italian and lives in `src/DreamBooks.App/Resources/Strings.resx`; never hard-coded.
- No network calls; no logging of document content.
- `dotnet build` with no warnings and `dotnet test` green at the end of every task.

## Review Focus

1. **Fast eraser strokes** — moving the back end quickly must erase everything along the path, not only at sampled positions (tested in Task 8 with a swept capsule).
2. **Huge render bitmaps at high zoom** — A4 at 800% on a 150%-scaled screen is ~9500×13500 px (≈500 MB BGRA); the render must cap pixel count and stretch instead (pinned in Task 10 by `RenderSizePolicy` tests).
3. **Back end touched while mode A/B wet renderer is enabled** — no wet pen ink may appear when erasing with the back end (Task 12 disables the renderer on `StylusInRange`/`InAirMove` when inverted; on the manual checklist).
4. **Strokes that leave and re-enter the page** — must produce clipped pieces, never ink outside the page (Task 7 test).
5. **Mouse and degenerate input** — mouse has no pressure (stored as `null`, drawn at base width), and a tap produces a one-point stroke that must render as a visible dot (Tasks 5, 6 tests).

---

## File Structure

```
DreamBooks.slnx
Directory.Build.props            shared: nullable, implicit usings, warnings as errors
Directory.Packages.props         central package versions
.editorconfig                    file-scoped namespaces, naming
src/DreamBooks.Core/
  Geometry/PointD.cs
  Model/InkColor.cs  InkTool.cs  InkPoint.cs  InkStroke.cs  Page.cs  Units.cs
  Undo/IUndoableCommand.cs  UndoManager.cs
  Commands/AddStrokesCommand.cs  EraseStrokesCommand.cs
  Ink/PressureCurve.cs  PressureMapping.cs  SmoothingLevel.cs  StrokeSmoother.cs
      StrokeOutline.cs  StrokeGeometry.cs  StrokeClipper.cs
      EraserMode.cs  EraserGeometry.cs  EraserSession.cs  PageEditor.cs
src/DreamBooks.Pdf/
  PdfRenderer.cs  PageSize.cs  RenderedPage.cs  PdfOpenError.cs  PdfOpenException.cs
  RenderSizePolicy.cs
  Interop/PdfiumNative.cs  Interop/PdfiumThread.cs
src/DreamBooks.App/
  App.xaml(.cs)  MainWindow.xaml(.cs)
  Resources/Strings.resx (+ Strings.Designer.cs)
  ViewModels/MainViewModel.cs  ToolsViewModel.cs  ZoomViewModel.cs
            AppTool.cs  RendererMode.cs  IFileDialogService.cs
  Services/FileDialogService.cs
  Views/PageView.xaml(.cs)        viewport: scroll, pan, pinch, Ctrl+wheel
  Ink/InkSurface.cs               the page element: bitmap, ink layers, stylus input
      CustomDynamicRenderer.cs    mode B wet ink
      CustomInkLayer.cs           mode B committed strokes
      StockStrokeConverter.cs     model → System.Windows.Ink.Stroke (mode A)
      StylusPointConverter.cs     stylus/mouse points → InkPoint
      Palette.cs
tests/DreamBooks.Core.Tests/  DreamBooks.Pdf.Tests/  DreamBooks.App.Tests/
```

`DreamBooks.App.Tests` is new relative to CLAUDE.md (view-model tests need a
`net10.0-windows` project); Task 1 adds it to CLAUDE.md's structure list.

---

### Task 1: Solution scaffold

**Files:**
- Create: `DreamBooks.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, the six project files, `tests/DreamBooks.Core.Tests/SmokeTests.cs`
- Modify: `.claude/rules/dotnet.md` (move "Proposed defaults" into the mandatory list), `CLAUDE.md` (add `tests/DreamBooks.App.Tests/` to the structure)

**Interfaces:**
- Produces: buildable solution; project references App → Core, Pdf; Pdf → nothing from Core; tests → their project.

- [ ] **Step 1:** `dotnet new sln -n DreamBooks` (produces `.slnx` on SDK 10), `dotnet new classlib` for Core and Pdf, `dotnet new wpf` for App, `dotnet new xunit` for the three test projects; add all to the solution and wire references. App.Tests targets `net10.0-windows`, `UseWPF=true`.
- [ ] **Step 2:** `Directory.Build.props`: `Nullable=enable`, `ImplicitUsings=enable`, `LangVersion=latest`, `TreatWarningsAsErrors=true`, `ManagePackageVersionsCentrally=true` (in `Directory.Packages.props`), `Platforms` unset. App: `RuntimeIdentifiers=win-x64;win-arm64`. Pdf: `AllowUnsafeBlocks=true`, `InternalsVisibleTo` DreamBooks.Pdf.Tests. Core: `InternalsVisibleTo` DreamBooks.Core.Tests.
- [ ] **Step 3:** `.editorconfig`: `csharp_style_namespace_declarations = file_scoped:warning`, standard .NET naming (private fields `_camelCase`).
- [ ] **Step 4:** Add packages: `CommunityToolkit.Mvvm` 8.4.2 to App; `bblanchon.PDFium.Win32` 158.0.8125 to Pdf. Delete template `Class1.cs`/`UnitTest1.cs`; add one trivial passing `SmokeTests.Solution_builds` in Core.Tests.
- [ ] **Step 5:** Run `dotnet build` → 0 warnings, 0 errors; `dotnet test` → passes; `dotnet format --verify-no-changes` → exit 0.
- [ ] **Step 6:** Update `.claude/rules/dotnet.md` and `CLAUDE.md` as listed above.
- [ ] **Step 7:** Commit `chore: solution scaffold for phase 1 (NFR-21, NFR-50)`.

---

### Task 2: Ink model

**Files:**
- Create: `src/DreamBooks.Core/Geometry/PointD.cs`, `Model/*.cs`
- Test: `tests/DreamBooks.Core.Tests/Model/InkStrokeTests.cs`, `PageTests.cs`, `UnitsTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct PointD(double X, double Y)` with `static double Distance(PointD a, PointD b)`.
  - `readonly record struct InkColor(byte A, byte R, byte G, byte B)`, `static InkColor FromRgb(byte r, byte g, byte b)`.
  - `enum InkTool { Pen, Highlighter }`.
  - `readonly record struct InkPoint(double X, double Y, float? Pressure, float? TiltX, float? TiltY, float? TipAngle, long TimestampMs)` with `PointD Position`. `null` = not reported by the device.
  - `sealed class InkStroke(Guid id, InkTool tool, InkColor color, double baseWidthPt, IReadOnlyList<InkPoint> points)`; properties `Id, Tool, Color, BaseWidthPt, Points`; `InkStroke WithPoints(IReadOnlyList<InkPoint> points)` returns a new stroke with a new Id and same attributes.
  - `sealed class Page(int index, double widthPt, double heightPt)`; `IReadOnlyList<InkStroke> Strokes`; `event EventHandler? StrokesChanged`; `internal void SetStrokes(IReadOnlyList<InkStroke> strokes)` (copies, raises event).
  - `static class Units`: `double MmToPt(double mm)`, `double PtToMm(double pt)`, `const double DipPerPt = 96.0 / 72.0`.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void Stroke_requires_at_least_one_point()
    => Assert.Throws<ArgumentException>(() => new InkStroke(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0,0,0), 1, []));
[Fact] public void Stroke_requires_positive_width() // baseWidthPt 0 → ArgumentOutOfRangeException
[Fact] public void Stroke_copies_points() // mutate source List<InkPoint> after ctor → stroke.Points unchanged
[Fact] public void WithPoints_keeps_attributes_and_changes_id()
[Fact] public void SetStrokes_replaces_and_raises_StrokesChanged_once()
[Fact] public void Page_requires_positive_size()
[Fact] public void MmToPt_converts() { Assert.Equal(72.0, Units.MmToPt(25.4), 9); Assert.Equal(1.417, Units.MmToPt(0.5), 3); }
```

- [ ] **Step 2:** `dotnet test tests/DreamBooks.Core.Tests` → FAIL (types missing).
- [ ] **Step 3:** Implement the types above.
- [ ] **Step 4:** `dotnet test tests/DreamBooks.Core.Tests` → PASS.
- [ ] **Step 5:** Commit `feat: ink stroke and page model in page points (FR-26, FR-27)`.

---

### Task 3: UndoManager

**Files:**
- Create: `src/DreamBooks.Core/Undo/IUndoableCommand.cs`, `UndoManager.cs`
- Test: `tests/DreamBooks.Core.Tests/Undo/UndoManagerTests.cs`

**Interfaces:**
- Produces: `interface IUndoableCommand { string Description { get; } void Do(); void Undo(); }`;
  `sealed class UndoManager(int capacity = 500)`: `void Execute(IUndoableCommand c)` (calls `Do`, pushes, clears redo), `bool Undo()`, `bool Redo()`, `bool CanUndo`, `bool CanRedo`, `void Clear()`, `event EventHandler? StateChanged` (raised after every Execute/Undo/Redo/Clear that changes anything).

- [ ] **Step 1: Failing tests** (use a `RecordingCommand` test double appending to a shared log)

```csharp
[Fact] public void Execute_runs_Do_and_enables_undo()
[Fact] public void Undo_then_Redo_replays_in_order() // log: do1 do2 undo2 undo1 do1
[Fact] public void New_command_after_undo_clears_redo() // FR-65: CanRedo false
[Fact] public void Undo_and_Redo_return_false_when_empty() // FR-64
[Fact] public void Keeps_at_least_200_steps_and_drops_oldest_beyond_capacity()
    // capacity 500: execute 501, undo 500 times → all true, 501st Undo false; first command never undone
[Fact] public void StateChanged_fires_on_execute_undo_redo_clear()
[Fact] public void Capacity_must_be_positive()
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement (a `LinkedList` or ring for the undo stack, `Stack` for redo). **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat: UndoManager with 500-step history (FR-60, FR-62, FR-64, FR-65)`.

---

### Task 4: Stroke commands

**Files:**
- Create: `src/DreamBooks.Core/Commands/AddStrokesCommand.cs`, `EraseStrokesCommand.cs`
- Test: `tests/DreamBooks.Core.Tests/Commands/StrokeCommandsTests.cs`

**Interfaces:**
- Consumes: `Page.SetStrokes`, `IUndoableCommand`.
- Produces: `sealed class AddStrokesCommand(Page page, IReadOnlyList<InkStroke> strokes)` — appends on top (z-order). `sealed class EraseStrokesCommand(Page page, IReadOnlyList<InkStroke> before, IReadOnlyList<InkStroke> after)` — `Do` sets `after`, `Undo` sets `before`. Both snapshot the page list they replace so `Undo` restores exact order. Descriptions: `"Add stroke"`, `"Erase"` (internal, not shown in UI).

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void Add_appends_and_undo_restores_previous_list()
[Fact] public void Add_then_undo_then_redo_restores_same_stroke_instances()
[Fact] public void Erase_undo_restores_original_order() // [a,b,c] → after [a,c]; undo → [a,b,c] by reference
[Fact] public void Mixed_sequence_of_50_operations_round_trips()
    // v1 acceptance criterion: random add/erase via UndoManager (seeded Random(42)),
    // record page.Strokes after each step, undo all 50 comparing to recorded states in reverse, redo all 50 comparing forward
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat: add and erase stroke commands (FR-63)`.

---

### Task 5: Pressure mapping and smoothing

**Files:**
- Create: `src/DreamBooks.Core/Ink/PressureCurve.cs`, `PressureMapping.cs`, `SmoothingLevel.cs`, `StrokeSmoother.cs`
- Test: `tests/DreamBooks.Core.Tests/Ink/PressureMappingTests.cs`, `StrokeSmootherTests.cs`

**Interfaces:**
- Produces:
  - `enum PressureCurve { Linear, Soft, Firm }`; `static class PressureMapping`: `const double MinFactor = 0.3, MaxFactor = 1.5`; `double Apply(PressureCurve curve, float pressure)` (clamps to 0–1; Linear `p`, Soft `p^0.6`, Firm `p^1.7`); `double WidthAt(double baseWidthPt, float? pressure, PressureCurve curve)` → `null` pressure returns `baseWidthPt`, else `base × (0.3 + 1.2 × Apply)`.
  - `enum SmoothingLevel { Off, Light, Strong }`; `static class StrokeSmoother`: `IReadOnlyList<InkPoint> Smooth(IReadOnlyList<InkPoint> points, SmoothingLevel level)`.

Smoothing algorithm (pinned here because the tests don't fully determine it):
1. Drop consecutive points closer than 0.01 pt (keep the first).
2. Light/Strong only: centred moving average of X, Y and Pressure with window 3 (Light) / 5 (Strong); first and last points unchanged; a point whose turning angle exceeds 70° is a **corner** and is left unchanged (preserves sharp letter corners, FR-23).
3. All levels: centripetal Catmull-Rom (alpha 0.5) through the resulting points, inserting samples so consecutive points are ≤ 0.5 pt apart; pressure and timestamp interpolated linearly; tilt/tip angle copied from the nearer source point.

- [ ] **Step 1: Failing tests**

```csharp
[Theory, InlineData(PressureCurve.Linear), InlineData(PressureCurve.Soft), InlineData(PressureCurve.Firm)]
public void Width_spans_0_3x_to_1_5x(PressureCurve c)
{ Assert.Equal(0.3, PressureMapping.WidthAt(1, 0f, c), 9); Assert.Equal(1.5, PressureMapping.WidthAt(1, 1f, c), 9); }
[Theory, ...] public void Curves_are_monotonic(PressureCurve c) // 1000 steps, never decreasing
[Fact] public void Soft_is_thicker_and_Firm_thinner_than_Linear_at_half_pressure()
[Fact] public void Missing_pressure_uses_base_width() // WidthAt(2, null, Linear) == 2
[Fact] public void Pressure_outside_range_is_clamped() // -0.2 → 0.3×, 1.4 → 1.5×

[Fact] public void Off_passes_through_every_input_point_in_order()
[Theory, InlineData(SmoothingLevel.Off), InlineData(SmoothingLevel.Light), InlineData(SmoothingLevel.Strong)]
public void Endpoints_are_preserved(SmoothingLevel l)
[Theory, ...] public void Consecutive_points_are_at_most_half_a_point_apart(SmoothingLevel l)
[Fact] public void Strong_keeps_sharp_corner() // L-shape (0,0)-(10,0)-(10,10) sampled every 1pt: output contains (10,0) within 0.05pt
[Fact] public void Single_point_returns_single_point() // a tap → dot
[Fact] public void Duplicate_points_produce_no_NaN()
[Fact] public void Pressure_is_interpolated_between_samples()
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat: pressure curves and stroke smoothing (FR-21, FR-22, FR-23)`.

---

### Task 6: Stroke outline geometry

**Files:**
- Create: `src/DreamBooks.Core/Ink/StrokeOutline.cs`, `StrokeGeometry.cs`
- Test: `tests/DreamBooks.Core.Tests/Ink/StrokeOutlineTests.cs`

**Interfaces:**
- Consumes: `PressureMapping`, `StrokeSmoother`.
- Produces:
  - `static class StrokeOutline`: `IReadOnlyList<IReadOnlyList<PointD>> Build(IReadOnlyList<PointD> centers, IReadOnlyList<double> widths)` — closed polygons (figures), to be filled together with the **non-zero** rule.
  - `static class StrokeGeometry`: `IReadOnlyList<IReadOnlyList<PointD>> BuildOutline(InkStroke stroke, PressureCurve curve, SmoothingLevel smoothing)` — smooth, width per point via `WidthAt(stroke.BaseWidthPt, p.Pressure, curve)`, then `Build`.

Algorithm: the outline is the union of a disc at every center (radius = width/2) and, for each consecutive pair whose discs don't contain each other, the quadrilateral between the two discs' external tangent points. Disc polygons use `n = clamp(ceil(2πr / 0.5), 8, 48)` vertices. **Every figure has the same winding** (positive shoelace area in y-down coordinates) so the non-zero fill is their union. This same output feeds the PDF appearance stream in phase 2.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void Single_point_gives_one_disc_of_the_given_width() // bounds ≈ 2×2 around center for width 2 (±0.05)
[Fact] public void All_figures_share_positive_winding()
[Fact] public void Output_contains_no_NaN_or_infinity() // includes duplicate centers and width 0.01
[Fact] public void Straight_constant_width_line_has_expected_bounds() // (0,0)-(10,0) width 2 → x ∈ [-1,11], y ∈ [-1,1] ±0.05
[Fact] public void Contained_disc_produces_no_quad() // centers 0.1 apart, widths 4 and 1 → only discs
[Fact] public void Pressure_changes_outline_width() // BuildOutline with pressure 0 vs 1 → bounds height ratio 0.3/1.5 ±5%
[Fact] public void Centers_and_widths_must_have_same_length()
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat: variable-width stroke outline geometry (FR-20, FR-22, FR-81 prep)`.

---

### Task 7: Clipping and PageEditor (commit path)

**Files:**
- Create: `src/DreamBooks.Core/Ink/StrokeClipper.cs`, `PageEditor.cs`
- Test: `tests/DreamBooks.Core.Tests/Ink/StrokeClipperTests.cs`, `PageEditorTests.cs`

**Interfaces:**
- Consumes: `Page`, `UndoManager`, `AddStrokesCommand`.
- Produces:
  - `static class StrokeClipper`: `IReadOnlyList<IReadOnlyList<InkPoint>> Clip(IReadOnlyList<InkPoint> points, double widthPt, double heightPt)` — empty if the **first** point is outside `[0,w]×[0,h]`; otherwise the inside runs, each segment crossing the edge cut at an interpolated edge point (pressure interpolated).
  - `sealed class PageEditor(Page page, UndoManager undo)`: `Page Page`; `bool CommitStroke(IReadOnlyList<InkPoint> points, InkTool tool, InkColor color, double baseWidthPt)` — clip, create one `InkStroke` per piece (new Guids), execute one `AddStrokesCommand` for all pieces; returns false (and executes nothing) if no piece survives. `EraserSession BeginErase(EraserMode mode)` (Task 8).

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void Stroke_inside_is_unchanged()
[Fact] public void Stroke_starting_outside_is_dropped()
[Fact] public void Stroke_leaving_page_is_cut_at_edge() // (90,50)->(110,50) on 100×100 → last point (100,50)
[Fact] public void Stroke_leaving_and_reentering_gives_two_pieces() // nothing stored outside the page
[Fact] public void CommitStroke_adds_one_undo_step_even_with_several_pieces()
[Fact] public void CommitStroke_outside_page_adds_nothing_and_returns_false()
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat: clip strokes to page and commit through undo (FR-27, FR-63)`.

---

### Task 8: Eraser geometry and session

**Files:**
- Create: `src/DreamBooks.Core/Ink/EraserMode.cs`, `EraserGeometry.cs`, `EraserSession.cs`
- Modify: `src/DreamBooks.Core/Ink/PageEditor.cs` (`BeginErase`)
- Test: `tests/DreamBooks.Core.Tests/Ink/EraserGeometryTests.cs`, `EraserSessionTests.cs`

**Interfaces:**
- Produces:
  - `enum EraserMode { WholeStroke, Partial }`.
  - `static class EraserGeometry`: `bool Hits(InkStroke stroke, PointD from, PointD to, double radiusPt)` — true if the stroke's polyline comes within `radiusPt + stroke.BaseWidthPt × 0.75` of the segment `from→to` (the eraser's swept capsule; 0.75 = half of the 1.5× max width). `IReadOnlyList<InkStroke> ErasePartial(InkStroke stroke, PointD from, PointD to, double radiusPt)` — resample the stroke so points are ≤ 0.25 pt apart, drop samples inside the capsule (distance ≤ radiusPt, no width margin), return the surviving runs as new strokes via `WithPoints`; runs shorter than 0.5 pt are discarded; if nothing is hit returns `[stroke]` (same instance).
  - `sealed class EraserSession` (created by `PageEditor.BeginErase`): `IReadOnlyList<InkStroke> Strokes` (working copy for the view); `bool MoveTo(PointD centerPt, double radiusPt)` — first call uses `from = to`, later calls sweep from the previous center; returns true if `Strokes` changed. `bool Complete()` — if anything changed, executes one `EraseStrokesCommand(page, before, Strokes)` and returns true. `void Cancel()`. The page is never modified before `Complete` (FR-33: only annotations exist in the model, so the PDF is untouchable by construction).

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void Hits_detects_stroke_crossed_between_samples()
    // stroke vertical line x=50; eraser moves (0,50)->(100,50) radius 1 in ONE MoveTo pair → hit (Review Focus 1)
[Fact] public void Hits_misses_distant_stroke()
[Fact] public void Partial_erase_in_the_middle_splits_into_two_strokes_with_new_ids()
[Fact] public void Partial_erase_at_the_end_shortens_the_stroke()
[Fact] public void Partial_erase_of_whole_stroke_returns_empty()
[Fact] public void WholeStroke_session_removes_hit_strokes_and_is_one_undo_step()
[Fact] public void Partial_session_with_many_moves_is_one_undo_step_and_undo_restores_original()
[Fact] public void Session_without_hits_adds_no_undo_step() // Complete returns false, CanUndo unchanged
[Fact] public void Page_is_unchanged_until_Complete()
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat: whole and partial eraser with swept hit test (FR-31, FR-33, FR-63)`.

---

### Task 9: PDFium renderer

**Files:**
- Create: `src/DreamBooks.Pdf/Interop/PdfiumNative.cs`, `Interop/PdfiumThread.cs`, `PdfRenderer.cs`, `PageSize.cs`, `RenderedPage.cs`, `PdfOpenError.cs`, `PdfOpenException.cs`
- Test: `tests/DreamBooks.Pdf.Tests/TestPdfFactory.cs`, `PdfRendererTests.cs`

**Interfaces:**
- Produces (public):
  - `readonly record struct PageSize(double WidthPt, double HeightPt)`.
  - `sealed record RenderedPage(int PixelWidth, int PixelHeight, int Stride, byte[] Bgra)`.
  - `enum PdfOpenError { FileNotFound, InvalidFormat, PasswordRequired, UnsupportedSecurity, Unknown }` mapped from `FPDF_ERR_FILE=2, FORMAT=3, PASSWORD=4, SECURITY=5`, else `Unknown`.
  - `sealed class PdfOpenException(PdfOpenError error) : Exception` with `Error`.
  - `sealed class PdfRenderer : IAsyncDisposable`: `static Task<PdfRenderer> OpenAsync(string path, CancellationToken ct = default)`; `int PageCount`; `PageSize GetPageSize(int pageIndex)` (all sizes read at open via `FPDF_GetPageSizeByIndexF`); `Task<RenderedPage> RenderPageAsync(int pageIndex, int pixelWidth, int pixelHeight, CancellationToken ct = default)`; `ValueTask DisposeAsync()` (closes the document on the worker).
- Internal: `PdfiumNative` with `[LibraryImport("pdfium")]` for `FPDF_InitLibrary`, `FPDF_LoadDocument` (path **UTF-8**, per the header), `FPDF_GetLastError`, `FPDF_GetPageCount`, `FPDF_GetPageSizeByIndexF`, `FPDF_LoadPage`, `FPDF_ClosePage`, `FPDF_CloseDocument`, `FPDFBitmap_CreateEx` (format `FPDFBitmap_BGRA = 4`), `FPDFBitmap_FillRect` (white `0xFFFFFFFF`), `FPDF_RenderPageBitmap` (flags `FPDF_ANNOT = 0x01`, no LCD text), `FPDFBitmap_GetBuffer`, `FPDFBitmap_GetStride`, `FPDFBitmap_Destroy`. Signatures: `build/native/include/pdfium/fpdfview.h` in the package. `PdfiumThread`: one background thread (`IsBackground = true`, named "PDFium") processing a queue; `static Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)`; calls `FPDF_InitLibrary` once on first use. Cancellation is checked before running a queued item.

- [ ] **Step 1: Test helper.** `TestPdfFactory.Write(string path, params (double w, double h)[] pages)` writes a minimal valid PDF (catalog, pages, one content stream per page drawing a filled black 100×100 pt square at the top-left with `0 0 0 rg x y 100 100 re f`, correct xref offsets) so no binary fixtures are committed. `TestPdfFactory.WriteGarbage(string path)` writes `"not a pdf"`.
- [ ] **Step 2: Failing tests**

```csharp
[Fact] public async Task Opens_and_reports_page_count_and_sizes() // pages (595,842),(842,595) → PageCount 2, sizes exact
[Fact] public async Task Missing_file_throws_FileNotFound()
[Fact] public async Task Garbage_file_throws_InvalidFormat() // NFR-11
[Fact] public async Task Render_returns_requested_size_and_stride() // 120×170 → Stride >= 480, Bgra.Length == Stride*170
[Fact] public async Task Render_draws_content() // pixel inside the black square is dark, bottom-right pixel is white
[Fact] public async Task Render_runs_off_the_calling_thread() // record Environment.CurrentManagedThreadId inside via internal hook or assert PdfiumThread.ThreadId != test thread
[Fact] public async Task Cancelled_render_throws_OperationCanceled()
[Fact] public async Task Path_with_non_ascii_characters_opens() // file "pàgina è.pdf"
```

- [ ] **Step 3:** Run `dotnet test tests/DreamBooks.Pdf.Tests` → FAIL. **Step 4:** Implement. **Step 5:** Run → PASS.
- [ ] **Step 6:** Commit `feat: PDFium page renderer on a dedicated thread (NFR-04, NFR-11, NFR-21)`.

---

### Task 10: Render size policy

**Files:**
- Create: `src/DreamBooks.Pdf/RenderSizePolicy.cs`
- Test: `tests/DreamBooks.Pdf.Tests/RenderSizePolicyTests.cs`

**Interfaces:**
- Produces: `static class RenderSizePolicy`: `const long MaxPixels = 24_000_000` (~96 MB BGRA); `(int Width, int Height) Compute(PageSize page, double zoom, double dpiScale)` → `round(widthPt × 96/72 × zoom × dpiScale)` etc., uniformly scaled down to fit `MaxPixels`, each side ≥ 1.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void A4_at_100_percent_and_150_dpi_scale() // (595,842), 1.0, 1.5 → (1190, 1684) ±1
[Fact] public void A4_at_800_percent_is_capped() // (595,842), 8.0, 1.5 → W×H ≤ 24_000_000, aspect preserved ±0.5%
[Fact] public void Tiny_zoom_gives_at_least_one_pixel()
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat: cap page render size at high zoom (NFR-06, NFR-07)`.

---

### Task 11: App shell, view models, debug panel

**Files:**
- Create: `src/DreamBooks.App/ViewModels/*.cs`, `Services/FileDialogService.cs`, `Resources/Strings.resx`, `Ink/Palette.cs`
- Modify: `src/DreamBooks.App/MainWindow.xaml(.cs)`, `App.xaml(.cs)`, `DreamBooks.App.csproj` (`NeutralResourcesLanguage` it)
- Test: `tests/DreamBooks.App.Tests/ToolsViewModelTests.cs`, `ZoomViewModelTests.cs`, `MainViewModelTests.cs`

**Interfaces:**
- Consumes: `PdfRenderer`, `PageEditor`, `UndoManager`, `PressureCurve`, `SmoothingLevel`, `EraserMode`, `Units`.
- Produces:
  - `enum AppTool { Pen, Eraser }`; `enum RendererMode { StockWpf, Custom }`.
  - `Palette.Colors`: 8 `InkColor` presets — black `#000000`, dark grey `#5A5A5A`, blue `#1F5BD8`, red `#D32F2F`, green `#2E7D32`, orange `#EF6C00`, purple `#7B1FA2`, brown `#6D4C41` (FR-25).
  - `ToolsViewModel : ObservableObject`: `AppTool ActiveTool` (default Pen); `bool IsInverted`; `AppTool EffectiveTool` (Eraser when `IsInverted`, else `ActiveTool`); `void BeginInvertedEraser()` / `void EndInvertedEraser()` (FR-30); `RendererMode Renderer` (default Custom); `PressureCurve Curve` (Linear); `SmoothingLevel Smoothing` (Light); `double BaseWidthMm` (0.5, clamped 0.25–5); `double BaseWidthPt`; `InkColor Color` (black); `EraserMode EraserMode` (WholeStroke); `double EraserSizeMm` (default 6, clamped 2–20); `bool IsCustomRenderer` (enables curve/smoothing controls).
  - `ZoomViewModel : ObservableObject`: `double Zoom` (1.0, clamped 0.25–8.0, FR-06); `ZoomInCommand`/`ZoomOutCommand` stepping through `25,33,50,67,75,100,125,150,200,300,400,600,800 %`; `void ZoomAt(double factor)` (pinch/wheel; clamped); `void FitWidth(double viewportWidthDip, double pageWidthPt)`.
  - `interface IFileDialogService { string? PickPdf(); bool Confirm(string message); }`.
  - `MainViewModel : ObservableObject`: ctor `(IFileDialogService dialogs)`; `ToolsViewModel Tools`; `ZoomViewModel ZoomVm`; `UndoManager Undo`; `PageEditor? Editor`; `PdfRenderer? Renderer`; `int PageNumber` (1-based), `int PageCount`; `string? ErrorMessage`; `IAsyncRelayCommand OpenPdfCommand`; `IAsyncRelayCommand<int> GoToPageCommand`; `IRelayCommand UndoCommand`, `RedoCommand` (CanExecute bound to `Undo.CanUndo/CanRedo`, refreshed on `StateChanged`). Opening or changing page with strokes present asks `Confirm(Strings.DiscardStrokesConfirm)`; on yes, creates a new `Page` + `PageEditor` and calls `Undo.Clear()`. `PdfOpenException` sets `ErrorMessage` from the resx (never throws to the UI). `internal Func<string, CancellationToken, Task<PdfRenderer>> OpenRenderer` seam for tests.
- Debug panel in `MainWindow.xaml`: left `Expander` (collapsible) with the controls listed in spec §6, all `Content`/labels bound to `Strings` via `{x:Static res:Strings.Key}`. Keyboard: `Ctrl+Z` undo, `Ctrl+Y` and `Ctrl+Shift+Z` redo (FR-61), `Ctrl+O` open. Right side hosts `PageView` (Task 12; until then an empty placeholder).
- Italian strings (key = text): `OpenPdf` = "Apri PDF", `Page` = "Pagina", `Renderer` = "Resa del tratto", `RendererStock` = "A · WPF standard", `RendererCustom` = "B · Personalizzata", `ToolPen` = "Penna", `ToolEraser` = "Gomma", `PressureCurve` = "Curva di pressione", `CurveLinear` = "Lineare", `CurveSoft` = "Morbida", `CurveFirm` = "Decisa", `Smoothing` = "Levigatura", `SmoothingOff` = "Nessuna", `SmoothingLight` = "Leggera", `SmoothingStrong` = "Forte", `Width` = "Spessore", `Color` = "Colore", `EraserMode` = "Modalità gomma", `EraseWhole` = "Tratto intero", `ErasePartial` = "Parte del tratto", `EraserSize` = "Dimensione gomma", `Undo` = "Annulla", `Redo` = "Ripristina", `Zoom` = "Zoom", `FitWidth` = "Adatta larghezza", `Panel` = "Pannello di prova", `DiscardStrokesConfirm` = "I tratti su questa pagina non sono salvati e andranno persi. Continuare?", `ErrorFileNotFound` = "Il file non è stato trovato.", `ErrorInvalidFormat` = "Il file non è un PDF valido o è danneggiato.", `ErrorPassword` = "Il PDF è protetto da password: non ancora supportato.", `ErrorUnsupported` = "Il PDF usa una protezione non supportata.", `ErrorUnknown` = "Impossibile aprire il PDF.", `NoDocument` = "Apri un PDF per iniziare".

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void Back_end_switches_to_eraser_and_restores_previous_tool() // Pen → Begin → Eraser → End → Pen (FR-30)
[Fact] public void Back_end_works_whatever_tool_is_active() // ActiveTool Eraser stays Eraser after End
[Fact] public void Base_width_is_clamped_and_converted() // 10 → 5; 0.1 → 0.25; BaseWidthPt == Units.MmToPt(BaseWidthMm)
[Fact] public void Curve_controls_enabled_only_for_custom_renderer()
[Fact] public void Zoom_steps_and_clamps() // ZoomIn from 1.0 → 1.25; Zoom = 20 → 8.0; Zoom = 0.1 → 0.25
[Fact] public void FitWidth_sets_zoom_from_viewport() // viewport 1000 DIP, page 600 pt → 1000 / (600×96/72) = 1.25
[Fact] public async Task Undo_redo_commands_follow_UndoManager_state() // FR-64
[Fact] public async Task Open_with_unsaved_strokes_asks_and_keeps_page_on_no()
[Fact] public async Task Open_damaged_pdf_sets_error_message_without_throwing() // NFR-11
```

- [ ] **Step 2:** Run `dotnet test tests/DreamBooks.App.Tests` → FAIL. **Step 3:** Implement view models, resx, panel XAML. **Step 4:** Run → PASS; `dotnet build` 0 warnings.
- [ ] **Step 5:** Commit `feat: prototype shell, debug panel and view models (FR-25, FR-30, FR-60, FR-61, FR-64)`.

---

### Task 12: Page surface — rendering, zoom, pen ink (modes A and B)

**Files:**
- Create: `src/DreamBooks.App/Views/PageView.xaml(.cs)`, `Ink/InkSurface.cs`, `Ink/CustomDynamicRenderer.cs`, `Ink/CustomInkLayer.cs`, `Ink/StockStrokeConverter.cs`, `Ink/StylusPointConverter.cs`
- Modify: `src/DreamBooks.App/MainWindow.xaml` (host `PageView`), `docs/requirements.md` (section 10, phase 1 line: add "zoom minimo (parte di FR-06) per verificare FR-26")
- Test: `tests/DreamBooks.App.Tests/StylusPointConverterTests.cs`, `StockStrokeConverterTests.cs`

**Interfaces:**
- Consumes: `MainViewModel`, `ToolsViewModel`, `ZoomViewModel`, `PageEditor`, `StrokeGeometry.BuildOutline`, `PressureMapping`, `RenderSizePolicy`, `PdfRenderer.RenderPageAsync`.
- Produces:
  - `StylusPointConverter`: `static InkPoint ToInkPoint(StylusPoint sp, double scale, long timestampMs, bool hasPressure)` — divides X/Y by `scale` (= `Zoom × Units.DipPerPt`) to get page points; pressure = `sp.PressureFactor` when the device reports `StylusPointProperties.NormalPressure`, else `null`; tilt from `XTiltOrientation`/`YTiltOrientation` (hundredths of a degree → degrees) when supported, else `null`; `TipAngle` null. `static InkPoint FromMouse(Point p, double scale, long timestampMs)` → pressure null. `static Point ToView(PointD pagePt, double scale)` (inverse, used by the cursor and tests).
  - `StockStrokeConverter`: `static Stroke ToWpfStroke(InkStroke s, double scale)` — points × scale, `DrawingAttributes { Color, Width = Height = BaseWidthPt × scale, FitToCurve = true, IgnorePressure = false, StylusTip = Ellipse }`; mouse strokes (null pressure) use `PressureFactor 0.5` and `IgnorePressure = true`.
  - `InkSurface : FrameworkElement` (the page itself; `Width/Height = page size × scale`, `ClipToBounds = true`): children in order — page `Image` (BitmapSource from `RenderedPage`, `Stretch.Fill`), `CustomInkLayer` (mode B committed), `InkPresenter` (mode A committed strokes in `Strokes`, and host of the active renderer's wet visual via `AttachVisuals(renderer.RootVisual, renderer.DrawingAttributes)`), eraser cursor (Task 13). Exposes dependency properties `Page`, `Scale`, `Mode`, `Curve`, `Smoothing`, `DisplayStrokes` (`IReadOnlyList<InkStroke>`; page strokes, or the eraser session's working copy in Task 13).
  - `CustomInkLayer : FrameworkElement`: one frozen `StreamGeometry` per stroke Id (figures from `StrokeGeometry.BuildOutline`, `FillRule.Nonzero`, in page points), drawn under a `ScaleTransform(scale)`; cache invalidated on curve/smoothing change; zoom changes only the transform.
  - `CustomDynamicRenderer : DynamicRenderer`: overrides `OnDraw(DrawingContext, StylusPointCollection, Geometry, Brush)` to draw, for the incoming points, discs and tangent quads with width `PressureMapping.WidthAt(baseWidthPt, pressure, curve) × scale` — the light "wet" stroke; the full smoothed outline replaces it on lift (the "settle" Fausto accepted). `Curve`, `BaseWidthPt`, `Scale`, `Color` set from the UI thread before contact.
- Behaviour:
  - Pen input: `StylusDown` (pen, not touch: `e.StylusDevice.TabletDevice.Type == TabletDeviceType.Stylus`) captures the stylus and starts collecting `e.GetStylusPoints(this)`; `StylusMove` appends; `StylusUp` converts and calls `Editor.CommitStroke(points, InkTool.Pen, Color, BaseWidthPt)`. Touch stylus events are ignored for ink (finger never draws).
  - Only one wet renderer is active: stock `DynamicRenderer` in mode A, `CustomDynamicRenderer` in mode B; switching swaps the plug-in in `StylusPlugIns` and re-attaches visuals. Renderer `Enabled = false` while `EffectiveTool` is Eraser or the stylus is inverted (set on `StylusInRange`, `StylusInAirMove`, `StylusDown`) — Review Focus 3.
  - Mouse (`e.StylusDevice == null` on mouse events) inks like the pen (NFR-23), wet stroke drawn on the UI thread into the `InkPresenter` via a temporary `Stroke`; promoted mouse events from pen/touch are ignored.
  - `Stylus.IsPressAndHoldEnabled`, `IsFlicksEnabled`, `IsTapFeedbackEnabled` all false on the surface.
  - Mode switch rebuilds `InkPresenter.Strokes` (A) or shows `CustomInkLayer` (B); both always reflect `DisplayStrokes`, refreshed on `Page.StrokesChanged`.
  - `PageView`: `ScrollViewer` (`PanningMode=None`) containing the `InkSurface` centred. `IsManipulationEnabled` on the viewport; `ManipulationDelta` from touch pans (scroll offsets) and pinch-zooms around `ManipulationOrigin` via `ZoomVm.ZoomAt`; `Ctrl+wheel` zooms around the pointer. After zoom stops changing for 150 ms, re-render the page with `RenderSizePolicy.Compute(pageSize, zoom, VisualTreeHelper.GetDpi(this).DpiScaleX)` on a cancellable token (previous render cancelled); until it arrives, the old bitmap stretches. Ink is vector and never waits for the render (NFR-07).

- [ ] **Step 1: Failing tests** for the two converters

```csharp
[Fact] public void Stylus_point_is_converted_to_page_points() // (200,100) at scale 2 → (100,50)
[Fact] public void Missing_pressure_becomes_null()
[Fact] public void Mouse_point_has_null_pressure()
[Fact] public void Wpf_stroke_is_scaled_and_keeps_color_and_width() // BaseWidthPt 1, scale 2 → Width 2
[Fact] public void Point_round_trip_through_view_is_stable() // page→view→page within 1e-9 at zooms 0.25, 1, 8 (FR-26)
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement converters → PASS. **Step 4:** Implement `InkSurface`, layers, renderers and `PageView`; update `docs/requirements.md` §10.
- [ ] **Step 5:** `dotnet build` 0 warnings; `dotnet test` all green; launch the app only if Fausto asks.
- [ ] **Step 6:** Commit `feat: page surface with pen ink in stock and custom modes, zoom and pan (FR-05, FR-06, FR-20, FR-22-FR-27, NFR-04, NFR-07, NFR-22)`.

---

### Task 13: Eraser input and cursor

**Files:**
- Modify: `src/DreamBooks.App/Ink/InkSurface.cs`
- Test: `tests/DreamBooks.App.Tests/MainViewModelTests.cs` (eraser routing)

**Interfaces:**
- Consumes: `PageEditor.BeginErase`, `EraserSession`, `ToolsViewModel.EffectiveTool/BeginInvertedEraser/EndInvertedEraser/EraserSizeMm/EraserMode`.
- Produces: `MainViewModel`: `EraserSession? ActiveErase`; `void BeginErase()`, `void EraseAt(PointD centerPt)` (radius = `Units.MmToPt(EraserSizeMm) / 2`), `void EndErase()` (calls `Complete`, clears `ActiveErase`).
- Behaviour: on `StylusDown` with `e.StylusDevice.Inverted` → `Tools.BeginInvertedEraser()`; on `StylusUp` after an inverted contact → `EndInvertedEraser()` (FR-30). When `EffectiveTool == Eraser` (tip with eraser tool, FR-34, or back end), down/move/up drive `BeginErase/EraseAt/EndErase` instead of inking; `DisplayStrokes` shows `ActiveErase.Strokes` during the gesture. Cursor (FR-32): a circle of the eraser diameter × scale, fill accent colour at 20% opacity with a 1 DIP outline, shown on `StylusInAirMove`/`StylusMove` when the effective tool is Eraser or the stylus is inverted, hidden on `StylusOutOfRange`. Mouse with the eraser tool erases too.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void Erase_gesture_is_one_undo_step() // commit 3 strokes, BeginErase, EraseAt across two, EndErase → Undo restores all 3
[Fact] public void Erase_uses_selected_size_and_mode() // Partial mode splits instead of removing
[Fact] public void EndErase_without_hits_adds_no_undo_step()
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS; `dotnet build` 0 warnings.
- [ ] **Step 5:** Commit `feat: back-end and tool eraser with cursor (FR-30, FR-31, FR-32, FR-34)`.

---

### Task 14: Release build and on-device handoff

**Files:**
- Modify: `CLAUDE.md` (commands table: add publish commands)

- [ ] **Step 1:** Run `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` → all clean.
- [ ] **Step 2:** Run `dotnet publish src/DreamBooks.App -c Release -r win-x64 --self-contained` → succeeds; run the same with `-r win-arm64` → succeeds (build only, NFR-21). Add both commands to CLAUDE.md's table (purpose: "Release build for on-device testing").
- [ ] **Step 3:** Commit `chore: release publish commands for phase 1 testing (NFR-21)`.
- [ ] **Step 4:** Give Fausto the manual checklist below (in Italian) and wait for results. Record his A/B decision in `docs/requirements.md` (e.g. a note under FR-23/FR-20 stating the chosen renderer and default curve/smoothing) in its own `docs:` commit.

Manual checklist (to translate for Fausto):
1. Open a real study PDF; page appears sharp; finger scroll/pinch moves and zooms, never draws (FR-05, FR-06, NFR-22).
2. Write a line of small handwriting in a margin in mode A, then the same in mode B with each curve and smoothing level; compare against OneNote fountain pen (FR-20–FR-23).
3. Write fast for 60 s: ink stays under the tip, no lag or stutter (FR-24, NFR-04, v1 acceptance).
4. Light → heavy pressure in one stroke: width grows smoothly from hairline to full (FR-20, FR-22).
5. Dots, commas, sharp corners survive; the lift "settle" in mode B is small and doesn't move letters.
6. Rest the palm while writing: no stray marks, no scrolling.
7. Flip the pen: back end erases with a visible circle, whatever tool is active; flip back: pen is back (FR-30, FR-32).
8. Whole-stroke vs partial eraser; erase fast across many strokes: nothing skipped (FR-31).
9. Undo/redo with buttons and Ctrl+Z/Ctrl+Y/Ctrl+Shift+Z across strokes and erases; buttons disable at the ends (FR-60–FR-65).
10. Zoom to 400–800% and back: strokes stay anchored to the text; page re-sharpens after a moment (FR-26, NFR-07).
11. Write past the page edge: ink is cut at the edge (FR-27).
12. Mouse draws and erases (NFR-23).
13. Decision: A, B, or B with which curve/smoothing as defaults.
