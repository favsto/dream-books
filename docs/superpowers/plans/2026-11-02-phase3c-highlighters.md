# Phase 3c Highlighters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A highlighter tool (round and chisel tips, six colours, straight-line mode) whose strokes are wide, translucent, saved as transparent annotations in the PDF, drawn under the pen ink, and recognised as marks when they cover a text line.

**Architecture:** Core gets the highlighter model (colours, optional chisel tip, constant-width and fixed-orientation chisel outlines, straight-line mode) and the highlighter-to-mark rule. Pdf writes highlighter annotations (route chosen by the task-0 spike) and reads them back. App adds the highlighter tool and its options in the view models, and draws highlighter strokes under the ink.

**Tech Stack:** unchanged (.NET 10, WPF, PDFium, CommunityToolkit.Mvvm, xUnit). No new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-11-02-phase3c-highlighters-design.md` (read sections 1a, 3, 4, 5, 6 and 8 first). Requirements: `docs/requirements.md` section 6 (FR-40..FR-46, FR-100).

## Global Constraints

- Core never references WPF/PDFium; PDFium only on the PDFium thread; the UI thread handles input and drawing (NFR-04); no new content is written into the PDF other than the highlighter annotations; the save verification of phases 2/3a is unchanged.
- The stroke JSON gains only optional fields; unknown keys stay ignored (test it); no schema version bump unless a reader rejects unknown keys (task 1 checks).
- Highlighter alpha: `HighlighterAlpha` = 89 (0.35 of 255) on every preset; the six presets (FR-41): yellow `#FFE14D`, green `#7BD88F`, light blue `#7CC4FF`, pink `#FF9BC4`, orange `#FFB366`, purple `#C8A2FF` (RGB; the alpha is applied on top).
- Constant width for highlighters (FR-42): no pressure mapping; `BaseWidthPt` is the nib length for chisel and the full width for round tips; the default round width is 6 pt, the chisel nib length 8 pt.
- Pen and eraser rules unchanged (FR-30, FR-34): the pen draws, the back end erases whatever the tool, fingers never draw, the highlight layer is not hit-testable.
- User-visible text only in `Strings.resx` (+ `Strings.Designer.cs`, drift test); values/colours/durations in `Theme.xaml`; NFR-32 respected (the highlight itself stays translucent by design; the note is in requirements).
- Nullable on; file-scoped namespaces; one public type per file; `_` prefix for private static readonly/const; warnings are errors; TDD in Core and Pdf; view models unit-tested; never run the WPF app; do not stage `.claude/settings.json`; commit trailer `Co-Authored-By: Claude Haiku 5.5 <noreply@anthropic.com>` (or the model that does the work); `dotnet build` 0 warnings, `dotnet test` green, `dotnet format --verify-no-changes` clean at the end of every task.
- Never log document content.

## Review Focus

1. **Transparency actually works in the saved file** in the reader Fausto uses; text under a highlight stays readable. Pinned in Task 0 (spike) and Task 5.
2. **Chisel geometry**: width depends on stroke direction exactly as specified; a horizontal and a vertical stroke get the expected widths for θ = 0° and 90°. Pinned in Task 2.
3. **Highlights never change pen ink**: pen strokes keep their pressure outline and their saved bytes; highlighters are drawn under pen ink on screen and in the saved order. Pinned in Tasks 3, 5, 7.
4. **Marks rule**: a highlight that covers a text line is a mark, a short one stays a note, margin lines are unaffected. Pinned in Task 4.
5. **Round trip**: save, reopen: tool, colour, alpha, chisel fields and straight-line result survive; the PDF fidelity check passes; pen strokes unchanged. Pinned in Task 5.

---

### Task 0: Transparency spike (go/no-go gate)

**Files:**
- Create: `tests/DreamBooks.Pdf.Tests/HighlighterOpacitySpikeTests.cs` (kept as a regression test of the chosen route)
- Create: `docs/superpowers/specs/2026-11-02-phase3c-opacity-spike-notes.md` (results)

**Interfaces:**
- Consumes: `PdfiumNative` ink annotation calls used by `StrokeAnnotations` (`FPDFPage_CreateAnnot`, `FPDFAnnot_SetRect`, `FPDFAnnot_SetColor` with alpha, `FPDFAnnot_SetBorder`, `FPDFAnnot_AddInkStroke`, `FPDFAnnot_SetAP`); `PdfRenderer.RenderPageAsync`.
- Produces: the chosen route, recorded in the notes file and as a constant `HighlighterWriteRoute` (enum `Route { CustomAppearance, PdfiumGenerated }`) in `src/DreamBooks.Pdf/HighlighterWriteRoute.cs`.

- [ ] **Step 1:** Write a test that creates one page with a text line (generated with `TextPdfFactory`), adds a highlight annotation written by route A (custom appearance filled with `#FFE14D`, colour alpha 89, multiple lines of content) and renders the page with PDFium; assert that a pixel inside the highlight over a dark text glyph is not fully covered (its luminance stays below the paper's) and that the annotation dictionary contains `/CA` 0.35. Write a second test for route B (`FPDFAnnot_SetBorder` wide, no custom AP) with the same asserts.
- [ ] **Step 2:** Run the two tests; record which route renders the text through the highlight and which keeps the `/CA`. Save the rendered PNG of both routes to the scratchpad (not the repo) and look at it with the image reader.
- [ ] **Step 3:** Write the notes file with the result. Decision rule: prefer route A if PDFium's render honours `/CA` in our appearance stream; else route B; if neither shows transparency, stop and ask Fausto (spec section 1a) before Task 5.
- [ ] **Step 4:** Create `HighlighterWriteRoute` with the chosen value; keep the spike tests as the regression of that route.
- [ ] **Step 5: Commit** `test: highlighter opacity spike and the chosen write route (FR-40)`.

---

### Task 1: Highlighter model, colours and the optional chisel field (Core)

**Files:**
- Create: `src/DreamBooks.Core/Model/HighlighterPresets.cs` (six `InkColor` constants with alpha `HighlighterAlpha` = 89), `src/DreamBooks.Core/Model/ChiselTip.cs` (`public readonly record struct ChiselTip(double AngleDeg, double Ratio)`)
- Modify: `src/DreamBooks.Core/Model/InkStroke.cs` (optional `ChiselTip? Chisel` property, default null; constructor overload or init property), `src/DreamBooks.Core/Model/StrokeJson.cs` (write `"chisel":{"angle":..,"ratio":..}` only when set; read it back; unknown keys ignored)
- Test: `tests/DreamBooks.Core.Tests/Model/HighlighterModelTests.cs`, extend `tests/DreamBooks.Core.Tests/Model/StrokeJsonTests.cs`

**Interfaces:**
- Produces: `HighlighterPresets.Yellow|Green|LightBlue|Pink|Orange|Purple` (and `HighlighterPresets.All`), `HighlighterPresets.Alpha = 89`; `InkStroke.Chisel`.

- [ ] **Step 1: Failing tests:**

```csharp
[Fact] public void Presets_are_six_distinct_colours_with_the_highlighter_alpha()          // All.Count == 6, distinct RGB, A == 89
[Fact] public void A_chisel_stroke_round_trips_through_JSON()                              // angle 45, ratio 0.25 survive
[Fact] public void A_stroke_without_chisel_writes_no_chisel_key()
[Fact] public void Unknown_root_keys_are_ignored_on_read()                                // a v1 payload with an extra key still parses
[Fact] public void A_chisel_with_an_angle_outside_0_to_180_is_rejected_at_construction()   // ArgumentOutOfRangeException
```

- [ ] **Step 2:** run, see the failures (types missing). **Step 3:** implement. **Step 4:** green; format. **Step 5: Commit** `feat: highlighter presets and optional chisel tip in the stroke model (FR-41, FR-46)`.

---

### Task 2: Highlighter geometry (Core): constant width, chisel outline, straight-line mode

**Files:**
- Create: `src/DreamBooks.Core/Ink/HighlighterGeometry.cs` (static: `ConstantWidthOutline(points, halfWidthPt)`, `ChiselOutline(points, chisel, lengthPt)`, `StraightenIfNearHorizontal(points)`), `src/DreamBooks.Core/Ink/StraightLine.cs` only if the helper needs a type
- Test: `tests/DreamBooks.Core.Tests/Ink/HighlighterGeometryTests.cs`

**Interfaces:**
- Produces: `static class HighlighterGeometry`:
  - `IReadOnlyList<(double X, double Y)> ConstantWidthOutline(IReadOnlyList<InkPoint> points, double widthPt)` — closed polygon, half-width `widthPt/2`, the same offsetting as `StrokeContour` but with a constant half-width.
  - `IReadOnlyList<(double X, double Y)> ChiselOutline(IReadOnlyList<InkPoint> points, ChiselTip chisel, double lengthPt)` — centre-line offset by `+h` and `−h`, `h = (L/2)·(cos θ, −sin θ)`, closed polygon (spec section 4).
  - `IReadOnlyList<InkPoint>? StraightenIfNearHorizontal(IReadOnlyList<InkPoint> points)` — returns the two end points when the chord is within 3° of horizontal and every point is within 2% of the chord length of it; otherwise `null`.

- [ ] **Step 1: Failing tests:**

```csharp
[Fact] public void Chisel_horizontal_stroke_at_0_degrees_is_thin_and_vertical_stroke_is_full_width()     // width perpendicular = ratio·L and L
[Fact] public void Chisel_at_90_degrees_swaps_the_horizontal_and_vertical_widths()
[Fact] public void Chisel_at_45_degrees_gives_the_expected_width_for_a_horizontal_stroke()               // L·(r + (1−r)·sin45°)
[Fact] public void Chisel_outline_is_closed_and_has_no_points_beyond_the_nib_envelope()
[Fact] public void Constant_width_outline_has_the_same_width_everywhere_regardless_of_point_spacing()
[Fact] public void Straightening_accepts_a_line_within_3_degrees_and_2_percent_and_rejects_a_wavy_one()
[Fact] public void Straightening_keeps_the_end_points_and_their_timestamps()
[Fact] public void A_stroke_with_fewer_than_two_points_is_not_straightened()
```

Width measurement helper in the test: measure the outline's extent perpendicular to the motion direction (project the polygon's points onto the normal of the stroke's chord; take max − min).

- [ ] **Step 2–4:** red → implement → green; format. **Step 5: Commit** `feat: highlighter constant-width, chisel and straight-line geometry (FR-42, FR-44, FR-46)`.

---

### Task 3: Creating highlighter strokes (Core ink and undo)

**Files:**
- Modify: `src/DreamBooks.Core/Ink/PageEditor.cs` (create strokes with the tool, colour and chisel of the active highlighter settings; straighten at pen-up when the straight-line setting is on, as an undoable command), `src/DreamBooks.Core/Ink/StrokeClipper.cs` and the `WithPoints`-style rebuilds of `InkStroke` (carry `Tool`, `Color`, `Chisel` through every rebuild: partial eraser, straightening, smoothing)
- Create: `src/DreamBooks.Core/Ink/HighlighterSettings.cs` (`public sealed record HighlighterSettings(InkColor Color, bool Chisel, double ChiselAngleDeg, bool StraightLine, double RoundWidthPt, double ChiselLengthPt)` with defaults: yellow, round, 45°, straight off, 6 pt, 8 pt; `ChiselRatio` 0.25 as a constant)
- Test: `tests/DreamBooks.Core.Tests/Ink/HighlighterStrokeTests.cs`

**Interfaces:**
- Consumes: Task 1 (`HighlighterPresets`, `ChiselTip`), Task 2 (`HighlighterGeometry`).
- Produces: `PageEditor.BeginHighlighter(HighlighterSettings)` / existing pen-stroke path reused with a `Tool = InkTool.Highlighter` stroke; `PageEditor.SetHighlighterSettings(HighlighterSettings)`. Straightening is a command (`AddStrokesCommand` replaced in the undo history by the straight version at pen-up).

- [ ] **Step 1: Failing tests:** a highlighter stroke has `Tool == Highlighter`, `Color.A == 89`, the chosen colour and `BaseWidthPt` 6 (round) or 8 (chisel, with `Chisel` set); pressure does not change its width; a pen stroke is unchanged; straight-line on makes the committed stroke two points and undo restores the drawn one; partial eraser keeps `Tool`, `Color` and `Chisel` on the pieces; `WithPoints`-style rebuilds keep them.
- [ ] **Step 2–4:** red → implement → green; the existing PageEditor/undo/eraser suites stay green; format. **Step 5: Commit** `feat: highlighter strokes in the page editor with undoable straight-line mode (FR-42, FR-44)`.

---

### Task 4: Highlights as marks (Core marks, FR-100)

**Files:**
- Modify: `src/DreamBooks.Core/Marks/MarkRules.cs` (`HighlightMinWidthPt` = 60, `HighlightMinHeightPt` = `DefaultLineHeightPt` (15)), `src/DreamBooks.Core/Marks/MarkSide.cs` (add `Centre`), `src/DreamBooks.Core/Marks/MarkRecognizer.cs` (`RecognizePage` takes highlighter strokes as the second input: clusters that touch or overlap vertically; a cluster with height ≥ 15 pt and width ≥ 60 pt becomes a mark with side `Centre` when its centre lies in the middle 36% of the page width, else `Left`/`Right` by centre; smaller clusters become notes; highlighter strokes never count as margin lines or margin notes)
- Modify (consumers of `MarkSide`): `src/DreamBooks.App/ViewModels/MarksViewModel.cs` and `MarkItem` (row automation name "al centro" via a new resx key), `src/DreamBooks.App/Resources/Strings.resx` + `Strings.Designer.cs`
- Test: `tests/DreamBooks.Core.Tests/Marks/HighlightMarkTests.cs`, extend `MarkRecognizerTests.cs` (margin lines unchanged), `tests/DreamBooks.App.Tests/MarksViewModelTests.cs` (centre row name)

**Interfaces:**
- `MarkRecognizer.RecognizePage(int pageIndex, double pageWidthPt, IReadOnlyList<InkStroke> strokes)` — strokes are split by `Tool` inside (the existing call sites pass the page's strokes; keep the signature).

- [ ] **Step 1: Failing tests:** a 16 pt high, 120 pt wide highlight in the middle is a `Centre` mark; the same at the margin is `Left`; a 14 pt tall highlight is a note; a 50 pt wide one is a note; two highlights touching vertically merge into one mark; a margin line next to a highlight is still a margin mark; highlighter strokes never appear in a margin-note list; exact boundaries (exactly 15 pt and 60 pt are marks).
- [ ] **Step 2–4:** red → implement → green; the 3b-1 suites stay green; format. **Step 5: Commit** `feat: highlights become marks when they cover a text line (FR-100)`.

---

### Task 5: Writing and reading highlighter annotations (Pdf)

**Files:**
- Modify: `src/DreamBooks.Pdf/StrokeAnnotations.cs` (write highlighter strokes with the route chosen in Task 0: the colour alpha from the stroke's `Color.A`; route A = our appearance stream with the fill colour; route B = PDFium-generated appearance; write highlighter annotations before pen annotations on each page; the private payload carries `tool` and `chisel` (already in the stroke JSON from Task 1))
- Modify: `src/DreamBooks.Pdf/PdfDocumentSaver.cs` only if the per-page order needs it (keep the save verification unchanged)
- Test: `tests/DreamBooks.Pdf.Tests/HighlighterAnnotationTests.cs`, round-trip test (use `PdfFidelity` and the existing save round-trip helpers)

- [ ] **Step 1: Failing tests:** save a page with one pen and one highlighter stroke (round and chisel, straight-line result), reopen: tool, colour (RGB and alpha), chisel angle/ratio and points are read back; the pen stroke's bytes/points unchanged; the annotation order in the saved file lists highlighters first; `PdfFidelity` finds no difference in page content or foreign annotations; the annotation carries `/CA` 0.35 (route A or B as chosen); the save of a document with only pen strokes is byte-for-byte the same payload as before (regression).
- [ ] **Step 2–4:** red → implement → green; the whole Pdf suite (round trip, recovery, fidelity) stays green; format. **Step 5: Commit** `feat: highlighter annotations saved transparent and read back as highlighters (FR-40, NFR-10)`.

---

### Task 6: Tool, options and view models (App)

**Files:**
- Modify: `src/DreamBooks.App/ViewModels/AppTool.cs` (add `Highlighter`), `ToolsViewModel.cs` (highlighter settings as `HighlighterSettings`: colour preset, round/chisel, angle 0–180 default 45, straight-line switch; effective tool and width for the wet renderer), `MainViewModel.cs` (apply the settings to the current page editor; the highlighter tool is a toggle with the pen rules: the pen draws it, the back end erases)
- Create: `src/DreamBooks.App/ViewModels/HighlighterOptionsViewModel.cs` (view-model for the options panel: commands for the six swatches, the shape switch, the angle (clamped 0–180), the straight-line switch; `AutomationProperties` names through resx)
- Modify: `src/DreamBooks.App/Resources/Strings.resx` + `Strings.Designer.cs` (tool name "Evidenziatore", six colour names in Italian, "Punta tonda", "Punta a scalpello", "Angolo", "Linea dritta", the options header)
- Test: `tests/DreamBooks.App.Tests/HighlighterOptionsViewModelTests.cs`, extend `ToolsViewModelTests.cs` (if present) or create it

- [ ] **Step 1: Failing tests:** selecting the highlighter tool changes the effective tool and keeps the pen's back-end erase; swatches set the colour and keep the alpha; the angle clamps to 0–180; the straight-line switch reaches the page editor; the options commands are keyboard reachable (IsCommand bound, names present in resx); Strings drift test.
- [ ] **Step 2–4:** red → implement → green; full App suite; format. **Step 5: Commit** `feat: highlighter tool and options in the view models (FR-41, FR-44, FR-46)`.

---

### Task 7: Drawing highlighters (App view)

**Files:**
- Modify: `src/DreamBooks.App/Ink/InkSurface.cs` and `src/DreamBooks.App/Ink/CustomInkLayer.cs` / `CustomDynamicRenderer.cs` (as needed: a highlighter layer below the pen ink layer for retained highlighter strokes; the wet renderer draws the highlighter in its colour with its alpha and its constant width or chisel outline while the stroke is live; eraser preview unchanged), `src/DreamBooks.App/Views/PageInkOverlay.cs` if it needs the same split
- Create: `src/DreamBooks.App/Ink/HighlighterLayer.cs` (retained drawing of highlighter outlines per stroke, `DrawingVisual` like the pen layer; not hit-testable)
- Test: `tests/DreamBooks.App.Tests/HighlighterDrawingTests.cs` (STA: layer order, transparency of the drawing, chisel outline in screen coordinates at the page scale, not hit-testable, eraser removes a highlighter stroke, pen still inks above it)

- [ ] **Step 1: Failing STA tests** as listed above, using the existing InkSurface test seams (`BeginPenStroke`/points/`FinishPenStroke`) with the highlighter tool.
- [ ] **Step 2–4:** red → implement → green; the existing ink, gating and rail pen tests stay green; format. **Step 5: Commit** `feat: highlighters drawn under the pen ink, with the same pen and eraser rules (FR-40, FR-42, FR-46)`.

---

### Task 8: Integration, requirement wording, publish and checklist (controller)

- [ ] **Integration test** (`tests/DreamBooks.App.Tests/HighlighterIntegrationTests.cs`): highlight three passages on a generated text PDF (one round, one chisel, one straight), save, reopen through the real view model, marks and ticks show the covering highlight as a centre mark, undo/redo; the real copy (skip when absent): highlight one passage on a page with text, numbers only.
- [ ] **Requirements (Italian, `docs/requirements.md`):** FR-40 (approximation: transparency via annotation opacity, no multiply; text tinted, not multiplied), FR-43 moved to Should, deferred (darkening on overlap), FR-45 Could deferred, FR-47/FR-48 deferred, FR-100 with the highlighter rule and the centre side. Tell Fausto what changed.
- [ ] **CLAUDE.md:** project structure for Core Ink highlighter types and `HighlighterGeometry`, Pdf `HighlighterWriteRoute`, App `HighlighterOptionsViewModel` and `HighlighterLayer`.
- [ ] **Publish** `dotnet publish src/DreamBooks.App -c Release -r win-x64 --self-contained -o src/DreamBooks.App/bin/Release/net10.0-windows/win-x64/publish-3c` (new folder; leave the others alone).
- [ ] **Italian checklist** for Fausto: highlight text in light and dark themes and check the text is readable; the six colours; round and chisel at 0°, 45°, 90° and straight-line; highlighter under pen ink; eraser on a highlight; highlight marks on the rail and in the list; save, reopen, and open the same file in another reader (Edge or Acrobat) to see the highlights; the overlap darkening (known limit).
