# Phase 3c spike notes: highlighter transparency in PDF annotations

Date: 2026-11-02. Task 0 of `docs/superpowers/plans/2026-11-02-phase3c-highlighters.md`, gate of spec section 1a
(FR-40). PDFium package as in phase 2 (`bblanchon.PDFium.Win32`), win-x64, .NET 10. The spike is kept as the
regression `tests/DreamBooks.Pdf.Tests/HighlighterOpacitySpikeTests.cs`; the choice is
`src/DreamBooks.Pdf/HighlighterWriteRoute.cs`.

## Verdict

**Route A (`CustomAppearance`) is chosen**, with one required detail: the appearance stream content must start with
`/GS gs`. The chisel outline (FR-46) can therefore be kept.

## What was tested

A 300 x 200 pt page (made with `TextPdfFactory`) with two words `mmmmmm` in black Helvetica 24 pt on one baseline. One
ink annotation highlights the first word only (band 10..140 x 81..111 pt), colour `#FFE14D`, colour alpha 89 through
`FPDFAnnot_SetColor`, written in the `StrokeAnnotations` order (Rect, Color, Border, InkList, Flags, SetAP last). The
file is saved with a full rewrite, reopened in a fresh PDFium instance (as another reader would; the annotation has no
`DRBK_Data`, so `PdfRenderer` draws it) and rendered at 200 % (600 x 400 px). Measured: the darkest pixel of the
glyph rows in the same columns under the band and in the second word outside it, and the average of the band rows
above the glyphs.

Three variants:

- **A-plain**: our appearance stream, a filled six-segment contour `1 0.8824 0.302 rg ... f`, as the spec described
  route A.
- **A-gs**: the same stream starting with `/GS gs`.
- **B**: no `SetAP`; `FPDFAnnot_SetBorder` width 30 pt, centreline of three points; PDFium (or the other reader)
  generates the appearance.

## Numbers

Luminance 0..255 (Rec. 709). Expected for 0.35 of `#FFE14D` over white: band blue ~193; over black text ~(89, 79, 27).

| Variant | Glyph under the band (min) | Same glyph outside (min) | Band luminance | Band blue | `/CA` in the annotation |
|---|---|---|---|---|---|
| A-plain | 220.7 (text hidden) | 0 | 220.7 | 77 (opaque) | `.34901962` |
| A-gs | 76.6 (text dark) | 0 | 242.6 | 192 | `.34901962` |
| B | 76.6 (text dark) | 0 | 242.6 | 192 | `.34901962` |

The PNGs of the three renders were checked visually (scratchpad, not in the repository): A-plain is a solid yellow
bar hiding the word, A-gs and B are a pale yellow band with the word clearly readable through it.

## Facts learned

- `FPDFAnnot_SetColor(..., a)` with `a < 255` writes `/CA a/255` (`.34901962` for 89) on the annotation.
- **`FPDFAnnot_SetAP` does add `/Resources`** when the annotation has `/CA` (the spec assumed it did not): the
  appearance XObject gets `/Resources << /ExtGState << /GS << /Type /ExtGState /CA .349 /ca .349 /BM /Normal
  /AIS false >> >> >>`. The name is `GS`, the blend mode is fixed to `Normal` (so multiply, FR-40, is still not
  available through the public API).
- PDFium does **not** apply the annotation's `/CA` to an appearance stream by itself: without `/GS gs` in the content,
  the fill is opaque even though both `/CA` and the ExtGState exist. With `/GS gs` it is translucent, identical to
  route B pixel for pixel in the measured areas.
- Order dependency: `SetColor` with the alpha must come before `SetAP` (it already does), otherwise there is no `/CA`
  and no ExtGState.
- Route B saves **no `/AP`**; the reader builds it from `/InkList`, `/C`, `/CA` and the border width. PDFium draws it
  with butt caps (the band ends at the centreline's ends) and a round line of constant width: no chisel.

## Open points for Task 5 and the on-device test

- Some readers apply the annotation's `/CA` on top of the appearance (PDF 32000 table 164 says `/CA` applies to the
  annotation's visible elements). With route A such a reader could compound the two to 0.35 x 0.35 ~ 0.12 (a fainter
  highlight, text still readable). PDFium's own generated appearance (route B once rendered) uses the same pattern
  (`/CA` on the dictionary plus a `/GS` ExtGState), so this is common practice; the Task 8 checklist (open the saved
  file in Edge or Acrobat) settles it on Fausto's reader.
- Task 5 must write `/GS gs` as the first operator of a highlighter appearance; the name `GS` comes from PDFium's
  `SetAP` and should be re-checked by this regression when the PDFium package version changes.
- Pen strokes are unaffected: they keep alpha 255 and no `/GS gs`.
