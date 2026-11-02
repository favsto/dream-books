# Phase 3c — Highlighters (round and chisel), saved transparent in the PDF

Date: 2026-11-02. Status: **written by Claude on Fausto's delegation ("procedi"); not yet read by Fausto.** Choices marked **[my call]** are mine and can be reversed. Requirement IDs refer to `docs/requirements.md` section 6 (Evidenziatore, Punta a scalpello) and section 9/10. Phases 3a and 3b are device-tested and merged on `main` (6e108cc).

## 1. Intent and the why

Fausto underlines and highlights passages while reading, and he uses those highlights to find them again (3b). Today the app has no highlighter tool: the stroke model already has `InkTool.Highlighter` and `StrokeJson` already reads/writes it, but nothing creates such strokes, and when a stroke is saved its colour is written opaque. 3c adds the highlighter as a real tool, with the look of a highlighter: wide, translucent, not hiding the text, and stored in the PDF so any reader shows it the same way.

## 1a. Open technical gate (decided by a spike, task 0 of the plan)

Whether a highlight can be transparent in **other readers** depends on how PDFium writes an annotation's opacity. The API gives us: the colour alpha (`FPDFAnnot_SetColor`, which writes the annotation's `/CA`), and an appearance stream we write ourselves (`FPDFAnnot_SetAP`, with no `/Resources`, so no `ExtGState` can be referenced from it). Two routes:
- **Route A:** our own appearance stream filled with the highlight colour, plus `/CA` from the colour alpha. Works only if the readers apply `/CA` to a custom appearance stream. Not verified yet.
- **Route B (fallback):** let PDFium build the ink appearance from `/InkList` and the border width (it adds the `ExtGState` for `/CA` itself). Transparency is then certain, but the nib is a round line: the chisel (FR-46) is lost, and the highlight width is the border width.

The spike renders both routes with PDFium and, where a second reader is available, checks them there. The plan's task 0 records which route is used. **If neither route gives transparency in the reader Fausto uses, this is a decision for Fausto before the writer is built** (options: accept an opaque, very light look; or ship without the chisel).

## 2. Goals, non-goals, anti-goals

**Goals (requirements)**
1. **FR-40** a wide translucent stroke that does not hide the text under it.
2. **FR-41** six presets: yellow, green, light blue, pink, orange, purple.
3. **FR-42** constant width, not pressure-dependent.
4. **FR-44** straight-line mode: a nearly straight stroke becomes horizontal.
5. **FR-46** chisel tip: fixed angle 0–180° (default 45°), adjustable tip proportions, in the options.
6. **FR-100 (second half)** a highlighted passage (at least one text line or three words) becomes a *mark* (3b-1 rule), and is never a note.
7. Highlighter strokes are saved in the PDF as annotations, transparent (opacity), readable by other readers, with the highlighter identity kept in our private data so the app reopens them as highlighters.

**Explicitly limited in 3c [my call, to be confirmed]**
- **FR-40 "moltiplica" is approximated.** PDFium's public API cannot create the blend-mode resource (`/BM Multiply`) in an annotation's appearance stream (spike notes, 30/10). 3c uses **annotation opacity** (`/CA`, set through the colour alpha) with light presets. Text under a highlight stays visible but is slightly tinted, not multiplied. Overlapping passes darken (FR-43 is therefore **deferred**, Should, not 3c). The spike (task 0 of the plan) verifies what PDFium writes and how it renders it, and records it.
- **FR-45** text-anchored highlight (snapping to words) is **deferred** (Could): it needs a text-selection UI.
- **FR-47 / FR-48** tip orientation following the pen tilt or barrel rotation: **deferred** (Could). The Slim Pen 2 does not transmit barrel rotation (requirements, Limite hardware). The chisel uses the fixed angle of FR-46 only.

**Anti-goals**
- Highlights that cover the text (too opaque), that stop the pen from writing over them, or that make the pen/eraser behave differently.
- Highlights saved opaque (today's bug) or as anything other than annotations: nothing new in the document content.
- Any change to the save verification of 3a/2 (the saver stays the same pipeline; highlighter annotations are written by the same stroke writer).
- Highlighters drawing above the pen ink on screen (pen ink stays on top, as in the PDF order).

## 3. Model and file format

- `InkStroke` already has `Tool` (`Pen`/`Highlighter`), `Color`, `BaseWidthPt` and points. Highlighter strokes use **constant width** (FR-42): `BaseWidthPt` is set from the tool setting and the pressure curve is bypassed. Defaults: round tip 16 pt wide, chisel nib 22 pt long (ratio 0.25, 45°), so one horizontal pass over a text line (15 pt) is a mark with either tip (section 6): the chisel at 45° shows 22 × (0.25 + 0.75 sin 45°) ≈ 17.2 pt.
- **Chisel [my call]:** a per-stroke optional `ChiselAngleDeg` (0–180) and `ChiselRatio` (tip thickness / length, default 0.25) written in the stroke's JSON as optional fields. Schema v1 readers that do not know the fields ignore them (the reader must be checked by a test: unknown keys ignored). **No schema version bump** unless a reader rejects unknown keys; if so, bump to v2 and keep reading v1.
- Colours: six presets as `InkColor` constants (`FR-41`), plus alpha for the highlighter's opacity **[my call]**: 0.35 for every preset, so light colours stay light and overlaps stay readable.
- **Opacity in the PDF:** the annotation's colour alpha (`FPDFAnnot_SetColor` alpha) is set from the stroke's alpha. Round trip test: save, reopen, the colour and alpha are read back (through the private payload, not the PDF colour alone).

## 4. Geometry

Conventions: page points, y down (as stored). Angles are measured in page space from the +x axis, counter-clockwise as seen on screen (so 90° is "pointing up").

- **Round tip (default):** constant-width contour from the smoothed centre-line: the existing `StrokeContour` is used with a constant half-width per point (no pressure mapping; `StrokeGeometry` gets an explicit constant-width path for highlighters, because it always applies `PressureMapping` today).
- **Straight-line mode (FR-44):** decided at pen-up. The stroke is straightened only if (a) the chord from first to last point is within 3° of horizontal and (b) every point lies within 2% of the chord length from that chord. Then the stored points are the two end points (the timestamps of the ends are kept). Otherwise the stroke is kept as drawn. Undoable like any command.
- **Chisel (FR-46), fixed orientation [decided]:** the nib is a segment of length `L` (= the stroke's `BaseWidthPt`) at angle `θ` (the tool setting, default 45°), with half-vector `h = (L/2)·(cos θ, −sin θ)` in page space (y down). The outline is the centre-line offset by `+h` and by `−h`, joined at both ends: a closed polygon filled with the nonzero rule. Its width measured perpendicular to a motion of direction `φ` is `L·(r + (1−r)·|sin(θ − φ)|)`, where `r` = `ChiselRatio` (default 0.25). So: a stroke parallel to the nib (φ = θ) is thin (`r·L`); a stroke perpendicular to it (φ = θ + 90°) is full width (`L`). Tests: θ=0°: horizontal stroke gives `r·L`, vertical stroke gives `L`; θ=90°: the reverse. The orientation is fixed for the whole stroke (FR-47/48 deferred).

## 5. Interaction (UI)

- A **highlighter tool** in the toolbar (`AppTool.Highlighter`), next to the pen: a toggle, same rules as the pen (pen draws it, finger never draws, back end of the pen erases, eraser unchanged).
- A small **options panel** (from the toolbar): six colour swatches (FR-41), round/chisel switch, chisel angle slider (0–180°, default 45°, FR-46), straight-line switch (FR-44). Italian text from resx; 40 px targets; keyboard reachable; values in Theme.xaml.
- Highlighter strokes are drawn **under** pen strokes on screen and in the saved order (the same z-order as the PDF annotation order: new highlighter annotations are written before new pen ones in the same save; existing annotations are never rewritten, see section 8).
- Eraser: the back end and the eraser tool erase highlighters like pen strokes (whole stroke; partial mode as today).
- The highlight layer is not hit-testable and the pen rules of 3a/3b hold.

## 6. Marks (3b integration, FR-100)

- The 3b-1 recogniser currently excludes highlighter strokes from margin lines and counts them as notes. 3c changes this: **a highlighter cluster (strokes that touch or overlap vertically) is a mark when its height is at least one text line (`MarkRules.DefaultLineHeightPt` = 15 pt) and its width is at least `MarkRules.HighlightMinWidthPt` = 60 pt** (about three short words). Smaller highlighter clusters stay notes. The thresholds are constants in `MarkRules`, tested on boundaries. The line height is the same fixed 15 pt the marks use (`IPageLineMetrics`); no text-layer metrics are used.
- Mark side: a new `MarkSide.Centre` value for highlights whose horizontal centre lies within the middle 36% of the page width (FR-100 margin rule does not apply to them). The list row name says "al centro" (resx); the rail tick and previous/next need no change (they use the vertical extent only). Highlights outside the middle band keep `Left` or `Right` by their centre.
- `RecognizePage` takes the highlighter strokes as a second input (the strokes it already classifies as notes today). A highlighter stroke never counts as a margin line or a margin-note stroke.

## 7. Testing

- Core TDD: constant width, colour presets and alpha, straight-line mode (within/outside 3°, undo-ready output), chisel nib geometry for θ = 0°, 45°, 90°, 180° and the ratio, highlighter-to-mark rule (one line, three words, short ones stay notes, pen strokes unaffected), JSON round trip of the new optional fields and unknown-key tolerance.
- Pdf: save/reopen round trip with highlighter annotations (colour, alpha, tool, chisel fields survive; pen strokes unchanged; page content identical with `PdfFidelity`); spike results recorded as a test that the annotation carries the opacity.
- App: tool switch, option panel view-model, round/chisel/straight modes through the pen seams, z-order on screen, eraser, marks rail tick for a highlighter mark; STA smoke.
- Integration: a generated text PDF with highlighters on three pages, save, reopen, marks and ticks; the real copy (skip when absent): highlight a passage on a page with text, numbers only.
- Manual checklist for Fausto (Italian): colours and transparency on text in light and dark themes; chisel feel at 0°, 45°, 90°; straight-line; highlighter under pen ink; eraser; highlighter marks on the rail; the saved file opened in another reader (Edge or Acrobat) shows the same highlights.

## 8. Known limits to tell Fausto

- Overlapping highlights darken (no multiply): FR-43 deferred.
- Text under a highlight is slightly tinted, not multiplied (FR-40 approximated).
- Barrel rotation not used (FR-48 deferred, hardware).
- Text-anchored highlight (FR-45) deferred.
- Annotation order in other readers: saving never rewrites existing annotations (NFR-10), so a highlighter added
  after a pen stroke was saved is appended after it and, in other readers, can paint over that pen ink (slightly
  tinted). Within one save, new highlighters are written before new pens. On screen the highlighter is always under
  the pen.

## 9. Requirement changes (same commit)

- FR-40, FR-43, FR-45, FR-47, FR-48: wording updated to the limits above; FR-43 moved to "Should, rinviato".
- FR-100 (3b): the highlighter-as-mark rule and the centred mark are written in the same commit.
- Section 10 phase 3: add "3c evidenziatori (3c)" as delivered in the same way as 3a/3b when it passes the device test.
