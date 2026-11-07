# Phase 5a: lasso selection (FR-28, FR-50, FR-51)

Fausto asked to proceed fast, so this design was written without a question round. Every decision below
is Claude's call unless marked "Fausto"; he can override any of them on reading.

## Phase 5 split

Phase 5 is four independent jobs, done as separate blocks, each with its own on-device test:

| Block | Content | Requirements |
|---|---|---|
| **5a** | Lasso selection and the operations on the selection | FR-28, FR-50, FR-51 |
| **5b** | Accessibility: contrast audit, UI Automation names, keyboard reachability, reduced effects | NFR-30, NFR-31, NFR-32 |
| **5c** | Performance on large PDFs: measure the 300-page reference file, then fix what the numbers show | NFR-02, NFR-03, NFR-06 |
| **5d** | MSIX package (x64 + ARM64), signing for personal use | distribution decision (open question) |

5a first: it is the only block that changes the document model, and the toolbar already has a disabled
Selection button waiting for it.

## Intent

Select ink, then move, resize, recolour, delete, copy and paste it, as one natural gesture on the Surface:
draw a loop around the strokes, then act on them. The pen's barrel button held down turns the pen
into the lasso for the length of the press (FR-28), the idea Fausto noted on 06/11/2026.

## Goals

- A freehand loop selects the strokes it encloses.
- Move, uniform resize, recolour, delete, copy and paste of the selection.
- Every operation is one undo step and survives save and reopen (round-trip tested).
- No new stored format: results are ordinary strokes.

## Non-goals

- Selecting across pages (a selection lives on one page).
- Selecting PDF text or the original page content (FR-45 stays deferred).
- Rotation, non-uniform stretching, grouping, z-order commands.
- Rectangle select (the loop is enough for v1).

## Anti-goals

- Selection must never cost pen latency (NFR-04): no work on the stylus path while writing with other tools.
- No operation may lose strokes silently: a transform that would leave a stroke entirely off the page is clamped, not dropped.

## Design

### Core (`DreamBooks.Core`, TDD)

- `Ink/SelectionGeometry`: point-in-polygon, loop simplification, stroke bounds.
  Rule (my call): a stroke is selected when at least 50% of its points lie inside the loop, so a long
  line brushing the loop's edge is not dragged along. The loop closes implicitly (last point to first).
  Highlighter strokes are selectable like pen strokes.
- `Ink/StrokeTransform`: translate and uniform scale about the selection bounds' centre, applied to
  points and to `BaseWidthPt` (width scales with the drawing, so a resized note keeps its look).
  Results are clamped so the selection's bounds stay on the page (translate) or inside it (scale).
  New strokes get new ids through `InkStroke.WithPoints` style construction.
- Edits go through `PageEditor` as new methods (`MoveStrokes`, `ScaleStrokes`, `RecolorStrokes`,
  `DeleteStrokes`, `PasteStrokes`), each building one replacement with the existing `EraseStrokesCommand`
  (or `AddStrokesCommand` for paste). The journal and the saver already handle replacements, so neither changes.
- A drag or resize is previewed live in the view and committed as one command at pen-up, never one per move.
- Copy buffer: an in-app `StrokeClipboard` in Core holding stroke copies (page-point coordinates).
  No system clipboard in v1. Paste puts the copy on the current page, offset by 12 pt from its
  original position, selected afterwards. It works across pages and documents of the same session.

### App (`DreamBooks.App`)

- `ToolbarTool.Selection` becomes active. The button enables; `InkTool` is untouched (selection is a mode,
  not a stroke type).
- `SelectionViewModel` (per document view): the selected stroke ids, bounds, state (none, selected,
  dragging, resizing), commands for recolour, delete, copy, paste. Unit-tested.
- `Ink/SelectionLayer` in `InkSurface`, above the ink: the live loop (dashed, accent colour), the selection
  rectangle with four corner handles, and the dimmed preview of the transformed strokes while dragging.
- Floating action bar on `FloatingSurface` near the selection: colour swatches (reuse `ColorSwatches`),
  copy, delete. Paste lives in the main menu and on the bar when the buffer is not empty and nothing is selected.
- Gestures: draw loop outside any selection to select; drag inside the rectangle to move; drag a corner
  to resize; tap outside to deselect. Changing tool, changing page by scrolling away, or undo that touches
  the selected strokes clears the selection.
- FR-28: while the barrel button is down the active tool is Selection; on release the previous tool returns,
  the same mechanism as the eraser end (existing "restore previous tool" path). Needs a device test
  because the button's event arrives from the stylus device, which cannot be tested here.
- UI text lives in `Strings.resx` (Italian): "Selezione", "Copia", "Incolla", "Elimina", "Colore".

### Tests

- Core: polygon containment, 50% rule, clamping, transform maths, each `PageEditor` operation, undo/redo of each.
- Pdf: round trip for moved, resized, recoloured and pasted strokes (save, reopen, compare strokes and page
  content), as `.claude/rules/testing.md` requires.
- App: `SelectionViewModel` state machine and commands.
- Manual checklist for Fausto on the Surface: loop feel, handle size with a finger and with the pen, barrel
  button, dimmed preview latency, pasting across pages.

## Left to the builder

Exact handle sizes (at least 44 px hit area for touch), loop simplification tolerance, animation of the
action bar (follows the existing floating-panel fade), icon glyphs from the existing Fluent icon set.

## Requirements changes

None to FR-28, FR-50, FR-51. The phase 5 split above is added to section 10 of `docs/requirements.md`.
