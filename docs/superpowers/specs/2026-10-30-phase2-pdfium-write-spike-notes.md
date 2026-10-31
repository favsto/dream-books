# Phase 2 spike notes: writing pen strokes with PDFium

Date: 2026-10-30. Source: a throwaway spike (not in the repository) run against `bblanchon.PDFium.Win32` 158.0.8125, win-x64, .NET 10. Fausto opened the spike's output files in the PDF readers he has and reported that everything looked right, including navigation and page rotation. The distilled facts below are what a builder needs; re-verify the numbered pitfalls when the PDFium package version changes (all annotation APIs are marked "Experimental" in the headers).

## Verdict

PDFium alone can write the strokes: ink annotations with a pressure-width appearance stream, a custom JSON key, saving, reopening, deleting, attachments, and files that use xref streams. Using the same engine to read and write means no second parser can reject a PDF that PDFium renders. PDFsharp is not used in phase 2.

## Facts measured

- A stroke of ~100 pt becomes one closed contour polygon of ~230-350 vertices. Written as an appearance stream it is Flate-compressed by PDFium (10 KB of contour text becomes ~3.7 KB).
- Cost per stroke in the file: ~6.8 KB (appearance stream + `/InkList` + the JSON payload), measured with an uncompressed JSON payload.
- 1,500 strokes on one page: adding them through the API took 3.7 s, saving 0.37 s, file 10 MB, reopen + load page 0.38 s, render 0.34 s, process peak memory ~102 MB.
- A JSON payload of 100 KB stored as an annotation string value round-trips exactly. It is written **uncompressed**; if it contains a single non-ASCII character the whole string is written as UTF-16BE and doubles in size, so write ASCII-only JSON (escape non-ASCII as `\uXXXX`).
- An embedded-file attachment (Flate-compressed) also round-trips, but readers show it in their attachments panel. Per-annotation JSON is preferred: it is self-contained, and if another reader deletes the annotation its data goes with it.
- Removing an annotation and saving works; with incremental saves the removed stroke stays physically in older revisions. A full rewrite (`FPDF_NO_INCREMENTAL`) produced a file about the size of the original (the spike checked only size, page and annotation counts, not text, links, outlines or forms).
- `FPDF_INCREMENTAL` re-appends **every object PDFium has loaded** (page, content streams, fonts, images), not only what changed, so the file grows at every save (e.g. +2.49 MB for one dot on a page with a 1.9 MB image). This is why phase 2 uses full rewrite on save (Fausto's decision), not incremental save.

## Call order and units (pitfalls)

- Order: `FPDFPage_CreateAnnot` (subtype `FPDF_ANNOT_INK` = 15) → `FPDFAnnot_SetRect` (`SetAP` returns false without a rect) → `SetColor` → `SetBorder` → `AddInkStroke` (centreline) → `SetStringValue` (our keys, `/NM`, `/M`, `/T`) → `SetFlags` (print = 4) → **`SetAP` last** → `FPDFPage_CloseAnnot`.
- `SetColor` fails once an appearance stream exists; `SetBorder` silently **deletes** the appearance stream: both must come before `SetAP`.
- Everything is in PDF user space, y up. `FS_RECTF` is `{left, top, right, bottom}` with top the larger y. The appearance stream is in page coordinates because BBox = Rect and there is no Matrix. Our model is top-left, y down: convert with `y' = pageHeight − y`, and handle page `/Rotate` and a non-zero MediaBox/CropBox origin (not tested by the spike; `FPDF_DeviceToPage` is the likely helper).
- Wide strings are UTF-16LE NUL-terminated. `GetStringValue` / `GetAP` return a byte length **including the NUL**: call once with a null buffer, then allocate `len / 2` chars. Keys are UTF-8 `const char*`. `FPDF_LoadDocument` takes a UTF-8 path.
- `FPDF_FILEWRITE` is `{int version = 1; int (*WriteBlock)(self, const void* data, unsigned long size)}`; Windows `unsigned long` is 32-bit; return non-zero on success. Use a `delegate* unmanaged` with an `[UnmanagedCallersOnly]` static method; keep the target stream in a `[ThreadStatic]` field.
- **File lock:** while a document is open with `FPDF_LoadDocument`, other processes can read it, but moving or replacing it fails (no FILE_SHARE_DELETE). "Write temp file, then replace the original" (NFR-10) therefore needs the document closed first, or `FPDF_LoadMemDocument64` (works, costs RAM equal to the file size), or `FPDF_LoadCustomDocument` over a `FileStream` opened with `FileShare.Delete` (not tested).
- **Rendering changes the document in memory:** an existing annotation without an appearance gets one generated on render, and a later save would write it. Never save from an instance that was rendered; use a separate instance for saving.
- PDFium is not thread-safe: every call stays on the one PDFium thread (NFR-04).
- Annotation dictionaries are written inline in the page's `/Annots` array (no setter for indirect objects, `/P`, `/IRT`). On a base that uses an xref stream, an incremental save appends an xref stream missing `/Type /XRef` (a full rewrite does not have this issue).
- Ink annotations are keyed by us with `/DBData` in the spike; a prefixed key (for example `/DRBK_Data`) is more polite. Other readers ignore unknown keys.

## Not tested by the spike

Encrypted PDFs, digitally signed PDFs (a full rewrite invalidates signatures), rotated pages and non-zero MediaBox/CropBox origins when writing, `FPDF_LoadCustomDocument`, highlighter transparency (`/CA` < 1 makes PDFium add an ExtGState; multiply blend is not available through the public API), full-rewrite fidelity for text, links, outlines and form fields.

## PDFium functions needed

Library and documents: `FPDF_InitLibrary`, `FPDF_GetLastError`, `FPDF_LoadDocument`, `FPDF_LoadMemDocument64`, `FPDF_CloseDocument`, `FPDF_GetPageCount`, `FPDF_LoadPage`, `FPDF_ClosePage`, `FPDF_GetPageWidthF`, `FPDF_GetPageHeightF`. Saving: `FPDF_SaveAsCopy` (`FPDF_NO_INCREMENTAL` = 2). Annotations: `FPDFPage_CreateAnnot`, `FPDFPage_GetAnnotCount`, `FPDFPage_GetAnnot`, `FPDFPage_RemoveAnnot`, `FPDFPage_CloseAnnot`, `FPDFAnnot_GetSubtype`, `FPDFAnnot_SetRect` / `GetRect`, `FPDFAnnot_SetAP` / `GetAP` (mode 0), `FPDFAnnot_SetFlags` / `GetFlags`, `FPDFAnnot_SetStringValue` / `GetStringValue` / `HasKey`, `FPDFAnnot_AddInkStroke`, `FPDFAnnot_GetInkListCount`, `FPDFAnnot_SetColor`, `FPDFAnnot_SetBorder`. Signatures: `FPDF_GetSignatureCount` (`fpdf_signature.h`). Rendering (already used): `FPDF_RenderPageBitmap` with flag `FPDF_ANNOT` = 1.
