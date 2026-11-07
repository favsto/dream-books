# Phase 5a Lasso Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Select ink with a freehand loop, then move, resize, recolour, delete, copy and paste it, each as one undo step (FR-28, FR-50, FR-51).

**Architecture:** Pure geometry and edit operations in `DreamBooks.Core` (TDD), expressed as page-stroke replacements through the existing `EraseStrokesCommand` / `AddStrokesCommand` so the journal, undo and saver need no change. A `SelectionViewModel` and a `SelectionLayer` in `InkSurface` drive the gestures; a floating action bar offers colour, copy, delete.

**Tech Stack:** .NET 10, WPF + WPF UI 4.3, CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-07-phase5a-lasso-design.md`

## Global Constraints

- Stroke coordinates stay in PDF page points; screen conversion only in the view layer (FR-26).
- Every model change goes through an `IUndoableCommand` on `UndoManager`; `DreamBooks.Core` references no WPF/PDFium.
- One public type per file, file named after the type; file-scoped namespaces; nullable enabled.
- UI text is Italian in `src/DreamBooks.App/Resources/Strings.resx`, never hard-coded.
- Conventional Commits citing requirement IDs; commits end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Done = `dotnet build` with no new warnings and `dotnet test` green. Do not run the app.
- No new NuGet packages.

## Review Focus

- Empty loop or loop of fewer than 3 points: selects nothing, changes nothing, no undo step.
- Loop that encloses a stroke of a single point (a dot): the dot is selectable.
- Move or scale that would push the selection off the page: clamped, no stroke lost or clipped away.
- Scale of a stroke group to a degenerate size (handle dragged through the opposite corner): clamped to a minimum scale of 0.2, never zero or negative.
- Undo or redo of a selection edit while the strokes are selected: selection clears instead of pointing at replaced strokes.
- Paste with an empty buffer: does nothing, no undo step. Paste near the page edge: stays inside the page.

---

### Task 1: Selection geometry

**Files:**
- Create: `src/DreamBooks.Core/Ink/SelectionGeometry.cs`
- Create: `src/DreamBooks.Core/Geometry/RectD.cs`
- Test: `tests/DreamBooks.Core.Tests/Ink/SelectionGeometryTests.cs`

**Interfaces:**
- Produces: `readonly record struct RectD(double Left, double Top, double Right, double Bottom)` with `Width`, `Height`, `Union(RectD)`, `Contains(PointD)`.
- Produces: `static class SelectionGeometry`:
  - `bool ContainsPoint(IReadOnlyList<PointD> loop, PointD p)` (even-odd, loop closed implicitly)
  - `IReadOnlyList<InkStroke> StrokesInLoop(IReadOnlyList<InkStroke> strokes, IReadOnlyList<PointD> loop)` (a stroke qualifies when at least 50% of its points are inside; returns [] for a loop under 3 points; keeps z-order)
  - `RectD? Bounds(IReadOnlyList<InkStroke> strokes)` (point bounds, null for none)

- [ ] **Step 1: Write failing tests** `ContainsPoint_square_inside_and_outside`, `StrokesInLoop_selects_stroke_fully_inside`, `StrokesInLoop_keeps_stroke_with_exactly_half_inside` and `..._drops_stroke_with_less_than_half`, `StrokesInLoop_selects_single_point_stroke`, `StrokesInLoop_returns_empty_for_two_point_loop`, `Bounds_is_null_for_empty`, `Bounds_covers_all_points`. Use a 100x100 square loop and strokes built with a small helper over `InkPoint`.
- [ ] **Step 2: Run** `dotnet test tests/DreamBooks.Core.Tests --filter SelectionGeometryTests`. Expected: compile failure / FAIL.
- [ ] **Step 3: Implement** the types above in the named files (ray-casting for containment).
- [ ] **Step 4: Run the same filter.** Expected: all PASS.
- [ ] **Step 5: Commit** `feat: lasso selection geometry (FR-50)`.

### Task 2: Stroke transforms

**Files:**
- Create: `src/DreamBooks.Core/Ink/StrokeTransform.cs`
- Test: `tests/DreamBooks.Core.Tests/Ink/StrokeTransformTests.cs`

**Interfaces:**
- Consumes: `RectD`, `SelectionGeometry.Bounds` (Task 1); `InkStroke(Guid, InkTool, InkColor, double, IReadOnlyList<InkPoint>, ChiselTip?)`.
- Produces: `static class StrokeTransform`:
  - `IReadOnlyList<InkStroke> Translate(IReadOnlyList<InkStroke> strokes, double dx, double dy, double pageWidthPt, double pageHeightPt)` — dx, dy are clamped so the bounds stay on the page; new ids.
  - `IReadOnlyList<InkStroke> Scale(IReadOnlyList<InkStroke> strokes, double factor, PointD anchor, double pageWidthPt, double pageHeightPt)` — uniform about `anchor`; `factor` clamped to at least 0.2 and to the largest factor that keeps the bounds on the page; scales `BaseWidthPt` by the applied factor; `Chisel` is carried over unchanged.
  - `IReadOnlyList<InkStroke> Recolor(IReadOnlyList<InkStroke> strokes, InkColor color)` — keeps tool and width; for highlighter strokes keeps the existing alpha (`A`) of the stroke and takes only R, G, B; new ids.

- [ ] **Step 1: Write failing tests** `Translate_moves_points_and_keeps_pressure_and_time`, `Translate_clamps_to_page_edge`, `Scale_doubles_distances_and_width_about_anchor`, `Scale_clamps_to_minimum_0_2`, `Scale_never_leaves_the_page`, `Scale_keeps_chisel`, `Recolor_pen_takes_new_colour`, `Recolor_highlighter_keeps_alpha`, `All_transforms_give_new_ids`.
- [ ] **Step 2: Run** filter `StrokeTransformTests`. Expected: FAIL.
- [ ] **Step 3: Implement** in `StrokeTransform.cs`; build new strokes with the public constructor (not `WithPoints`, which drops nothing but cannot change width or colour).
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat: stroke translate, scale and recolour (FR-51)`.

### Task 3: Page edit operations and clipboard

**Files:**
- Create: `src/DreamBooks.Core/Ink/StrokeClipboard.cs`
- Modify: `src/DreamBooks.Core/Ink/PageEditor.cs` (add methods below)
- Test: `tests/DreamBooks.Core.Tests/Ink/PageEditorSelectionTests.cs`, `tests/DreamBooks.Core.Tests/Ink/StrokeClipboardTests.cs`

**Interfaces:**
- Consumes: `StrokeTransform` (Task 2), `EraseStrokesCommand(Page, before, after, description)`, `AddStrokesCommand`.
- Produces on `PageEditor`, each returning the ids of the resulting selected strokes (`IReadOnlyList<Guid>`, empty and no undo step when nothing changed):
  - `MoveStrokes(IReadOnlyCollection<Guid> ids, double dx, double dy)` description "Move"
  - `ScaleStrokes(IReadOnlyCollection<Guid> ids, double factor, PointD anchor)` description "Resize"
  - `RecolorStrokes(IReadOnlyCollection<Guid> ids, InkColor color)` description "Recolour"
  - `DeleteStrokes(IReadOnlyCollection<Guid> ids)` description "Delete selection"
  - `PasteStrokes(StrokeClipboard clipboard)` → adds copies offset by 12 pt (clamped) via `AddStrokesCommand`
  - `IReadOnlyList<InkStroke> Strokes(IReadOnlyCollection<Guid> ids)` — the current page strokes with those ids, in z-order.
- Produces: `sealed class StrokeClipboard` with `bool IsEmpty`, `IReadOnlyList<InkStroke> Strokes`, `void Set(IReadOnlyList<InkStroke> strokes)`, `event EventHandler? Changed`.
- Replacement keeps the z-order position of each transformed stroke.

- [ ] **Step 1: Write failing tests** `Move_is_one_undo_step_and_undo_restores_order`, `Move_with_unknown_ids_does_nothing`, `Scale_is_one_undo_step`, `Recolor_is_one_undo_step`, `Delete_removes_only_selected`, `Paste_empty_clipboard_does_nothing`, `Paste_offsets_by_12pt_and_stays_on_page`, `Paste_works_on_another_page`, `Redo_after_undo_reapplies`; clipboard: `Set_raises_Changed_and_IsEmpty_is_false`.
- [ ] **Step 2: Run** filters `PageEditorSelectionTests` and `StrokeClipboardTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Each edit builds `before = Page.Strokes` and `after` with the replaced strokes in place, then `_undo.Execute(new EraseStrokesCommand(Page, before, after, description))`.
- [ ] **Step 4: Run.** Expected: PASS. Then run all Core tests.
- [ ] **Step 5: Commit** `feat: move, resize, recolour, delete and paste as undo steps (FR-51)`.

### Task 4: Round trip through save and reopen

**Files:**
- Test: `tests/DreamBooks.Pdf.Tests/SelectionEditsRoundTripTests.cs`

**Interfaces:**
- Consumes: `PageEditor` operations (Task 3). Follow the pattern of the existing round-trip tests in `tests/DreamBooks.Pdf.Tests` (find them with `grep -l RoundTrip`); reuse their helpers.

- [ ] **Step 1: Write the tests** `Moved_strokes_survive_save_and_reopen`, `Scaled_strokes_keep_width_after_reopen`, `Recoloured_highlighter_keeps_transparency_after_reopen`, `Deleted_strokes_are_gone_after_reopen`, `Pasted_strokes_on_other_page_survive`. Each saves, reopens, compares stroke points (within existing round-trip tolerance), colour, width and the page's original content.
- [ ] **Step 2: Run** `dotnet test tests/DreamBooks.Pdf.Tests --filter SelectionEditsRoundTripTests`. Expected: PASS on first run (no saver change); if any FAIL, that is a real bug in the saver or transforms: stop and investigate before continuing.
- [ ] **Step 3: Commit** `test: round trip of selection edits (FR-51, FR-83)`.

### Task 5: Selection view model

**Files:**
- Create: `src/DreamBooks.App/ViewModels/SelectionViewModel.cs`
- Create: `src/DreamBooks.App/ViewModels/SelectionState.cs` (enum `None, Selected, Moving, Resizing`)
- Test: `tests/DreamBooks.App.Tests/SelectionViewModelTests.cs`

**Interfaces:**
- Consumes: `PageEditor`, `StrokeClipboard`, `SelectionGeometry`, `StrokeTransform`.
- Produces: `SelectionViewModel(Func<PageEditor?> currentEditor, StrokeClipboard clipboard)` (ObservableObject) with:
  - `SelectionState State`, `IReadOnlyList<InkStroke> Selected`, `RectD? Bounds`, `IReadOnlyList<InkStroke> Preview` (transformed copies during a drag, else same as Selected)
  - `void SelectLoop(IReadOnlyList<PointD> loop)` (empty result → state None)
  - `void BeginMove()`, `void MoveBy(double dx, double dy)`, `void BeginResize(PointD anchor)`, `void ScaleTo(double factor)`, `void Commit()`, `void Cancel()` — preview only until `Commit`, which calls exactly one `PageEditor` operation and re-selects the resulting ids
  - `void Recolor(InkColor)`, `void Delete()`, `void Copy()`, `void Paste()`, `void Clear()`
  - `bool CanPaste` (clipboard not empty), `bool HasSelection`
  - clears itself when the editor's page changes or when `UndoManager` raises a command-applied event touching that page (take the editor's `Page.StrokesChanged`; ignore the change it caused itself).

- [ ] **Step 1: Write failing tests** `SelectLoop_selects_enclosed_strokes`, `SelectLoop_with_nothing_inside_stays_None`, `Move_previews_without_touching_the_page_until_Commit`, `Commit_move_is_one_undo_step`, `Cancel_restores_the_page`, `Recolor_Delete_Copy_Paste_commands`, `Undo_clears_selection`, `Copy_then_Paste_on_other_page`, `No_editor_means_commands_do_nothing`.
- [ ] **Step 2: Run** filter `SelectionViewModelTests`. Expected: FAIL.
- [ ] **Step 3: Implement** as above.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat: selection view model (FR-50, FR-51)`.

### Task 6: Enable the Selection tool

**Files:**
- Modify: `src/DreamBooks.App/ViewModels/AppTool.cs` (add `Selection`), `ToolbarViewModel.cs:67,99,176,214` (enable button, map `ToolbarTool.Selection` ↔ `AppTool.Selection`, `IsEnabled` returns true), `ToolbarTool.cs` (update the doc comments), `ToolsViewModel.cs` (see FR-28 below).
- Modify: `src/DreamBooks.App/ViewModels/MainViewModel.cs` — own a `StrokeClipboard` and a `SelectionViewModel`; expose them; switching away from Selection calls `Selection.Clear()`.
- Modify: `src/DreamBooks.App/Resources/Strings.resx` + `Strings.Designer.cs`: `SelectionCopy` "Copia", `SelectionPaste` "Incolla", `SelectionDelete` "Elimina", `SelectionColor` "Colore" (`ToolSelection` already exists).
- Test: `tests/DreamBooks.App.Tests/ToolbarViewModelTests.cs` (extend; locate the existing file first)

**Interfaces:**
- Consumes: `SelectionViewModel` (Task 5).
- Produces: `MainViewModel.Selection`, `MainViewModel.Clipboard`. `ToolsViewModel` gains `bool IsBarrelHeld` / `SetBarrelHeld(bool)`: while held, `EffectiveTool` is `AppTool.Selection`; on release the remembered tool returns. Mirror the existing `_isInverted` mechanism exactly (FR-28), but the eraser end wins when both apply.

- [ ] **Step 1: Write failing tests** `Selection_button_is_enabled`, `Tapping_selection_activates_it`, `Barrel_held_makes_effective_tool_selection_and_release_restores_previous`, `Eraser_end_wins_over_barrel`, `Leaving_selection_clears_the_selection`.
- [ ] **Step 2: Run** filter `ToolbarViewModelTests|ToolsViewModelTests`. Expected: FAIL.
- [ ] **Step 3: Implement** the changes listed.
- [ ] **Step 4: Run** the full App tests. Expected: PASS (existing assertions that Selection is disabled must be updated, not deleted).
- [ ] **Step 5: Commit** `feat: Selection tool enabled with barrel-button activation (FR-28, FR-50)`.

### Task 7: Selection layer, gestures, action bar

**Files:**
- Create: `src/DreamBooks.App/Ink/SelectionLayer.cs` (draws the live loop dashed in the accent colour, the bounds rectangle with four corner handles of at least 44 px hit area, and the dimmed preview of `Selection.Preview`)
- Create: `src/DreamBooks.App/Views/SelectionActionBar.xaml` + `.xaml.cs` (on `FloatingSurface`: `ColorSwatches`, Copy, Delete; Paste button when `CanPaste` and no selection)
- Modify: `src/DreamBooks.App/Ink/InkSurface.cs` (route stylus/mouse/touch input to the layer when the effective tool is Selection; screen ↔ page-point conversion stays here), `Views/DocumentView.xaml(.cs)` (host the action bar near the selection bounds), main menu entry "Incolla" in `MainMenuButton`.

**Interfaces:**
- Consumes: `MainViewModel.Selection` (Task 5/6). Gestures: draw a loop outside the selection to `SelectLoop`; drag inside the bounds → `BeginMove`/`MoveBy`/`Commit` at pen-up; drag a corner → `BeginResize(oppositeCorner)`/`ScaleTo`/`Commit`; tap outside → `Clear`. Nothing on the stylus path of the other tools changes (NFR-04).

- [ ] **Step 1: Implement the layer and gestures**, following `HighlighterLayer`/`SearchHighlightLayer` for layer registration and `InkSurface` for input routing.
- [ ] **Step 2: Implement the action bar** and wire it; strings only from `Strings`.
- [ ] **Step 3: Run** `dotnet build` (no new warnings) and `dotnet test`. Expected: green.
- [ ] **Step 4: Commit** `feat: lasso layer, gestures and action bar (FR-50, FR-51)`.

### Task 8: Docs, release build, device checklist

- [ ] **Step 1:** In `CLAUDE.md` project structure add the new files (SelectionLayer, SelectionActionBar, SelectionViewModel; Core SelectionGeometry, StrokeTransform, StrokeClipboard). Mark 5a "in prova" in section 10 of `docs/requirements.md`.
- [ ] **Step 2:** `dotnet format --verify-no-changes`, `dotnet build`, `dotnet test`. Then `dotnet publish src/DreamBooks.App -c Release -r win-x64 --self-contained`.
- [ ] **Step 3: Commit** `docs: phase 5a structure and status (FR-28, FR-50, FR-51)`.
- [ ] **Step 4:** Give Fausto this checklist and wait for results before calling 5a done:
  1. (FR-50) With Selection active, draw a loop round three strokes: they are boxed, others untouched.
  2. (FR-50) A loop that only grazes a long line does not select it.
  3. (FR-51) Drag the selection: it follows the pen without lag; lifting commits one undo step.
  4. (FR-51) Drag a corner with the pen and with a finger: handle is easy to hit; the writing keeps its look when resized.
  5. (FR-51) Change colour on pen and on highlighter strokes; highlighter stays translucent.
  6. (FR-51) Delete, undo, redo.
  7. (FR-51) Copy, scroll to another page, paste: appears slightly offset, selected, undoable.
  8. (FR-28) Hold the barrel button and draw a loop: lasso works; releasing returns the previous tool.
  9. (FR-83) Save, close, reopen: moved/resized/recoloured strokes are as left, and still erasable.
  10. (NFR-04) Writing with the pen after using the lasso feels as before.
