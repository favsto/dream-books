# Phase 1 — Ink prototype: design

Date: 2026-10-30 · Status: approved by Fausto (2026-10-30)
Requirements: `docs/requirements.md`, section 10 phase 1; FR-20–FR-27, FR-30–FR-34,
FR-60–FR-65, NFR-04, NFR-21, NFR-22, NFR-23.

## 1. Intent

Phase 1 exists to answer one question on the real device: **does writing in
Dream Books feel like writing on paper?** Everything else in the app depends
on that answer, so it comes first, with the least UI possible.

Fausto's reference for "right" is the **fountain pen in OneNote**. His
reference for "wrong" is inking on PDFs in **Edge/Chrome**: irregular stroke,
visible lag, no pressure, and finger and pen confused (it scrolls when it
should write, writes when it should scroll).

What "right" means, in his words played back and confirmed:

1. **Fidelity to the gesture.** The stroke follows the pen tip exactly: no
   broken segments, no over-smoothing that changes the shape of letters, no
   visible delay between tip and ink.
2. **Depth of pressure.** Width changes continuously across the whole range:
   a light touch gives a hairline, pressing gives a full stroke.
3. **Finger and pen never confused.** The pen always writes, the finger
   always moves the view. Never the other way round.

Typical use: notes in the margins and between lines of a PDF's text,
underlining, occasional lines and simple drawn shapes. Small handwriting
next to printed text is the demanding case.

## 2. Goals

- Open a PDF and show one page, rendered by PDFium, sharp at the current
  zoom and screen scale (NFR-22).
- Write on it with the pen, with pressure (FR-20, FR-22), smoothing
  (FR-23), ink appearing under the tip while moving (FR-24, NFR-04).
- **Two stroke renderers to compare on the device**, switchable at any time:
  - **Mode A — stock WPF.** WPF's own ink rendering (`InkCanvas`-style,
    `DrawingAttributes` with pressure, `FitToCurve`).
  - **Mode B — custom.** Our own pressure curve and smoothing, drawn as a
    variable-width filled outline, still on the Windows Ink / WPF real-time
    stylus path so latency stays minimal.
- Eraser on the pen's back end, whatever tool is active, restoring the
  previous tool on lift (FR-30); whole-stroke (default) and partial erase
  (FR-31); eraser tool usable with the tip (FR-34); semi-transparent circle
  showing the eraser area (FR-32); erases only annotations (FR-33).
- In-memory undo/redo through `UndoManager` and `IUndoableCommand`: buttons,
  Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z, disabled when empty, ≥ 200 steps, a new
  stroke clears redo (FR-60–FR-65 minus FR-63's move/colour/bookmark cases,
  which do not exist yet).
- Strokes stored in PDF page points from the first line of code (FR-26),
  belonging to one page and clipped to its edge (FR-27).
- A **spartan debug panel** (approved by Fausto) to tune and compare.
- At the end, Fausto picks the renderer to carry forward (A, B, or B with
  specific tweaks). That decision is written into `docs/requirements.md`.

## 3. Non-goals (later phases)

Saving or reloading (phase 2), autosave/recovery, multiple pages and
virtualized rendering, bookmarks, highlighter (all phase 3); floating Fluent
toolbar, Mica/Acrylic, animations, themes, start screen, settings persistence
(phase 4); lasso selection, FR-28 (phase 5). No WPF UI (Fluent) package in
this phase. No free colour picker (presets only).

## 4. Anti-goals — failure even if it "works"

- Any perceptible lag between tip and ink, or ink that appears only on lift.
- The finger draws, or the pen scrolls/zooms.
- Smoothing that rounds off small details: dots, the tail of a comma, sharp
  corners of small letters must survive (FR-23 "without losing precision in
  small details").
- Width that feels stepped or saturates early instead of using the full
  pressure range.
- Strokes drifting from where they were drawn after a zoom (FR-26).
- Throwaway work: both modes must sit on the same model and commands, so
  whatever wins carries into phase 2 unchanged.

## 5. Decisions

### Made with Fausto

- Phase 1 scope as in the requirements: one page, pen, back-end eraser,
  in-memory undo/redo. Highlighter stays in phase 3.
- A/B comparison of stock WPF ink vs a custom renderer, decided on device.
- Debug panel can be spartan; looks come in phase 4.
- PDF library choice delegated to Claude, priority: widest range of PDFs
  opened correctly, within the license and platform rules.

### Claude's calls (Fausto can redirect)

- **PDF rendering: PDFium native binaries from `bblanchon.PDFium.Win32`**
  (Apache-2.0 / BSD-3, current PDFium, i.e. the engine in Chrome/Edge — the
  widest real-world compatibility available) plus a **thin P/Invoke layer of
  our own** in `DreamBooks.Pdf` covering only what phase 1 needs: init,
  load document (with error reporting), page count, page size in points,
  render page to BGRA bitmap, close. Rejected: Docnet.Core and PDFiumCore
  (no win-arm64 binaries, breaks NFR-21), PDFtoImage (pulls in SkiaSharp,
  too heavy for the 8 GB dev machine), PdfiumViewer (WinForms, abandoned).
  **Before adding it, the builder verifies the package contains
  `runtimes/win-arm64` and asks Fausto** (CLAUDE.md rule on NuGet).
- PDFium is not thread-safe: all PDFium calls go through **one dedicated
  background worker thread** inside `DreamBooks.Pdf`; the UI thread never
  calls PDFium (NFR-04).
- Packages to propose to Fausto at plan time: `bblanchon.PDFium.Win32`,
  `CommunityToolkit.Mvvm` (MIT), xUnit + test SDK (Apache-2.0/MIT). Nothing
  else.
- **Minimal zoom in phase 1**: Ctrl+wheel, pinch with two fingers, and
  +/− buttons in the panel, range 25–800% (subset of FR-06); one-finger pan
  when the page is larger than the window. Reason: small handwriting needs
  to be tested at several zoom levels, and FR-26 anchoring can only be
  verified with zoom. After zoom settles, the page is re-rendered at the
  new resolution in the background; ink is redrawn from the model
  immediately (NFR-07 spirit).
  Because this moves part of FR-06 from phase 3 into phase 1, the commit
  that adds zoom also updates the phase 1 line in section 10 of
  `docs/requirements.md`.
- **Mode A is built from WPF's stock pieces, not the real `InkCanvas`
  control.** `InkCanvas` keeps and edits its own stroke list, which breaks
  the rule that every model change goes through `IUndoableCommand`. Mode A
  uses the same parts `InkCanvas` uses internally: the stock
  `DynamicRenderer` for the wet stroke and `Stroke`/`DrawingAttributes`
  rendering for committed strokes. Palm rejection and pen/touch separation
  come from the WPF stylus stack, not from `InkCanvas`, so they are the same
  in both modes. This keeps mode A visually identical to stock WPF ink, and
  either winner carries into phase 2.
- **Input routing**: pen tip → active tool; pen back end → eraser; finger →
  pan/zoom only, never ink; mouse → behaves like the pen (NFR-23). Palm
  rejection relies on WPF/Windows Ink stylus handling. The "draw with finger"
  setting is out of scope.
- **Mode switch re-renders all existing strokes** in the selected mode, so
  the same handwriting can be compared side by side. Both modes also differ
  in the live (wet) stroke while drawing. Points are always captured and
  stored the same way regardless of mode.
- In mode A, the custom pressure curve and the 0.3×–1.5× mapping (FR-21,
  FR-22) **cannot be enforced**: WPF maps pressure to width internally. Mode
  A uses the same base width and colour and otherwise WPF's behaviour; this
  gap is part of what the comparison shows.
- **Mode B stroke model**: width(p) = BaseWidth × (0.3 + 1.2 × curve(p))
  (FR-22). Curves: linear, soft, firm (FR-21) — exact formulas left to the
  builder, must be monotonic with curve(0)=0 and curve(1)=1. Smoothing:
  Bézier/spline through the captured points, with a smoothing level in the
  panel (off / light / strong) for tuning; "off" must still be continuous,
  not polyline-jagged at normal writing speed.
- **Wet vs dry stroke in mode B (decided by Fausto):** a small visible
  "settle" when the pen lifts is acceptable, as OneNote does. The quality of
  the final stroke matters more than the live stroke matching it exactly.
  So the wet stroke can be a lighter, cheaper rendering on the real-time
  stylus thread (it must still follow the tip with pressure and no lag),
  and on lift it is replaced by the full smoothed outline from Core. Core's
  outline geometry is therefore a whole-stroke function, not an incremental
  one. The settle must stay small: the final stroke must not visibly shift
  position or change the shape of letters.
- **The stroke-outline geometry for mode B lives in `DreamBooks.Core`**
  as pure math (points + widths → closed outline polygon in page points).
  It is unit-tested, and phase 2 reuses it for the PDF appearance stream
  (requirements section 7), so the stroke on screen and in the saved PDF
  will be identical.
- Base width range 0.25–5 mm (FR-22), stored in points
  (mm × 72 / 25.4). Default 0.5 mm. Colours: the 8 presets of FR-25, default
  black; the exact 8 colours are the builder's choice.
- **Clipping (FR-27)**: a stroke that starts outside the page is ignored;
  a stroke leaving the page is clipped at the edge (rendering clip and
  stored points; builder decides whether to cut points or clip geometry,
  as long as nothing outside the page is stored or drawn).
- **Erasing**: one eraser gesture (down → up) is **one undo step**, whatever
  it touched. Partial erase splits strokes into the surviving pieces; split
  and hit-test geometry live in Core and are unit-tested. Eraser size: one
  fixed default (builder's choice) with a size control in the panel.
- **Undo/redo**: history limit 500 (≥ 200 per FR-62); the oldest steps are
  dropped beyond it.
- `InkPoint` keeps all fields from the data model (X, Y, Pressure, TiltX,
  TiltY, TipAngle, Timestamp); fields the device does not report are stored
  as "not available", not as fake zeros. Tilt is captured now even though
  nothing uses it yet.
- **Which PDF and page**: an "Apri PDF" button (file dialog) and a page
  number field in the panel; only the selected page is shown and inked.
  Switching page or file discards the in-memory strokes (no saving in
  phase 1) — the builder adds a simple confirmation if strokes exist.
  Damaged/unsupported PDFs show a clear message without crashing (NFR-11).
- Code conventions: adopt the "proposed defaults" in
  `.claude/rules/dotnet.md` (nullable on, file-scoped namespaces, one
  public type per file, `Async` + `CancellationToken`). Fausto should confirm
  them while reviewing this document.
- Delivery for the on-device test: `dotnet run` on the Surface (which is
  also the dev machine), plus a self-contained `dotnet publish` for x64 in
  Release, so the feel is judged on an optimized build. An ARM64 publish
  must build, but is not tested on a device in this phase.

## 6. Debug panel

Small, plain WPF panel docked at one side, collapsible. All text in Italian
from `.resx`. Contents:

- Apri PDF · page number
- Renderer: A (WPF) / B (custom)
- Tool: Penna / Gomma
- Pressure curve: lineare / morbida / decisa (B only; disabled in A)
- Smoothing: off / light / strong (B only)
- Base width slider, 0.25–5 mm, with value
- 8 colour swatches
- Eraser: tratto intero / parte del tratto · size
- Undo · Redo (disabled when empty)
- Zoom: − / value % / + / fit width

## 7. Structure

As planned in CLAUDE.md: `DreamBooks.App` (WPF, views, view models, input
routing, both renderers), `DreamBooks.Core` (model, commands, UndoManager,
pressure curves, outline geometry, eraser geometry, coordinate transforms
between page points and view), `DreamBooks.Pdf` (PDFium interop and
renderer), plus the two xUnit test projects. Core has no WPF/PDFium
reference.

## 8. Testing

- TDD in Core: model invariants, page↔view coordinate transform round
  trips, UndoManager (limit, redo cleared by new command, enabled states),
  add-stroke and erase commands, pressure curves and the 0.3×/1.5× bounds,
  outline geometry (closed, no NaN, width honoured, tiny strokes and single
  dots produce visible output), partial-erase splitting, hit testing,
  clipping.
- TDD in Pdf: open valid, damaged and missing files; page size in points;
  rendered bitmap dimensions for a given scale. The test PDF fixtures are
  small files committed under the test project.
- View models unit-tested (tool switching, back-end eraser restoring the
  previous tool, undo/redo enabled states).
- Feel and visuals: numbered manual checklist for Fausto on the Surface,
  citing requirement IDs, ending with the A/B choice.

## 9. Left to the builder

Exact pressure-curve formulas, smoothing algorithm and its levels, the 8
preset colours, eraser default size, panel layout, how the wet stroke is
drawn in mode B (DynamicRenderer subclass or equivalent, as long as it stays
on the real-time stylus thread), render cache details, test fixture
contents, and the task breakdown.

## 10. Done means

Build with no new warnings, all tests green, manual checklist run by Fausto
on the Surface, his A/B decision recorded in `docs/requirements.md`, and his
approval to merge into `main`.
