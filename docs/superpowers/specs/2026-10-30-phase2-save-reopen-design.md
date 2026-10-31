# Phase 2 — Save, reopen and crash recovery: design

Date: 2026-10-30 · Status: approved and implemented (branch `feature/phase2-save-and-reopen`; on-device test pending; Fausto's real-PDF folder: `C:\Users\Favsto\workspace\temp`)
Requirements: `docs/requirements.md` section 7 and section 10 phase 2; FR-80, FR-81, FR-82, FR-83, FR-84 (changed), FR-85, FR-86 (changed), FR-89, NFR-10, NFR-11, NFR-12, NFR-20; FR-04 (position restore, partly). Read together with `docs/superpowers/specs/2026-10-30-phase2-pdfium-write-spike-notes.md`, which records what a spike proved about writing strokes with PDFium.

## 1. Intent

Fausto annotates PDFs in two situations: work documents and long technical or leisure e-books. His one fear is **losing information**: an annotation he cannot find again, or finds again unreadable. Phase 2 makes the prototype's strokes durable. When he decides to save, the strokes go into the PDF itself so that any reader shows them and this app can edit them again. Between saves, and after any crash, nothing he drew is lost: he restarts where he was.

For this phase the files are **local files only**. How a file arrived (Drive, mail, chat) and how it leaves (he uploads by hand) is out of scope.

## 2. Goals

- **Save** (Ctrl+S) rewrites the opened file; **Save as** (Ctrl+Shift+S) writes a new file (FR-80). Saving runs off the UI thread and never blocks writing; a discreet indicator shows it is working (FR-89).
- **Safe replacement** (NFR-10): the new file is written to a temporary file next to the original, checked, and only then replaces the original. At no moment is the user's only copy half written.
- **Visible everywhere** (FR-81): each stroke becomes an ink annotation whose appearance is the filled contour of the stroke (the same polygon the app draws, `StrokeGeometry.BuildOutline`), so any reader shows the pressure-dependent width.
- **Editable again** (FR-83): each annotation carries the stroke's data (points, pressure, colour, tool, width) as JSON with a schema version; on reopen the app rebuilds editable strokes, which can be erased and undone.
- **Original content intact** (FR-82): text, links, forms, outlines and existing annotations of the original PDF are preserved.
- **Journal** (FR-84, changed): every unsaved change is recorded in a local journal as it happens. After a crash, battery loss or OS crash the app reopens the document where Fausto was (page, zoom, scroll position) with the same strokes, marked "modified, not saved".
- **Whole document model**: strokes of every page live in the document and survive page changes (phase 1 threw them away when the page changed); one undo history per document.
- Close with unsaved changes asks Save / Don't save / Cancel (FR-85); "Don't save" deletes the journal.

## 3. Non-goals

Cloud/Drive/mail awareness; highlighter, bookmarks, multi-page scrolling and zoom UI (phase 3); the final floating UI (phase 4); lasso selection; flattened export (FR-87); password-protected PDFs (they keep failing to open with the phase 1 message); editing or deleting annotations made by other programs; an automatic timed save (Fausto saves when he decides); incremental saving; PDFsharp.

## 4. Anti-goals — failure even if the tests pass

- Any path where the user's original file is lost, truncated or left half written, or where a failed save reports success.
- A saved file that a PDF reader shows without the strokes, with a repair prompt, or with different widths.
- Strokes that reopen as plain images, or that reopen duplicated (once from the file and once from the journal).
- The file growing at every save (the reason incremental saving was rejected).
- Strokes from other programs being modified or deleted; or our strokes being drawn twice (once in the page bitmap, once in the ink layer).
- A journal that is silently applied to a different file than the one it was written for; a journal that survives a "Don't save" answer; a journal that is lost because the last write was torn by the crash.
- Stray temporary files left next to the user's PDF; the document content in logs (NFR-12).
- Saving or journal writing that makes pen input lag.

## 5. Decisions

### Made with Fausto

- Local files only in this phase.
- PDFium writes the strokes; PDFsharp is not used. (`docs/requirements.md` section 2 changes accordingly.)
- Saving **rewrites the whole file** (no incremental save, so the file never grows); the safety comes from write-to-temp, verify, replace.
- No timed autosave: the user decides when to save.
- A local journal keeps unsaved changes; on a normal close where the user refuses to save, the journal is deleted; after an abnormal end it stays and is applied at the next start.
- Journal also tracks erasing strokes that were loaded from the file (strokes have stable Ids).
- Only strokes this app wrote can be erased; annotations from other programs are shown but not editable.
- After a crash the app resumes with the same strokes at the same position if possible.
- Fausto almost never annotates digitally signed PDFs, so a full rewrite that invalidates a signature is acceptable, as long as the saving step stays replaceable (see the interface call below) and the user is warned.

### Claude's calls (Fausto can redirect)

- **Interface for saving.** Saving sits behind an interface (for example `IDocumentSaver`) with one implementation now (full rewrite via PDFium), so a future append-only writer can be added without touching the rest. If the PDF contains a digital signature (`FPDF_GetSignatureCount` > 0), the app warns before saving that saving invalidates it, and offers Save as.
- **Strokes in the file.** One ink annotation per stroke: `/Subtype /Ink`, `/Rect` = contour bounding box, appearance stream = the contour polygon filled with the stroke colour, `/InkList` = the centreline (so readers that ignore the appearance still show something), `/C` colour, print flag, `/NM` = stroke Id, `/M` date, `/T` = "Dream Books", and a private key (name chosen with a recognisable prefix, for example `/DRBK_Data`) holding ASCII-only JSON `{"v":1,"id":...,"tool":...,"color":...,"baseWidthPt":...,"points":[{x,y,p,tx,ty,t}...]}`. Not an attachment (it would show in readers' attachments panels). Points are stored once in the JSON; the centreline in `/InkList` may be simplified.
- **How the file is produced.** From a **separate PDFium document instance used only for saving**: load the current file on disk, remove the app-created annotations whose Id is no longer in the model, add annotations for strokes that are new, leave existing and foreign annotations alone, write the whole file with `FPDF_NO_INCREMENTAL` to a temp file in the same folder. The viewer's render instance is never saved (rendering alters the in-memory document).
- **Verification before replacement**: reopen the temp file with PDFium and check page count, the number of app annotations per page equals the model's strokes per page, every JSON payload parses and equals what was written, the count of foreign annotations per page is unchanged, and text extraction of the first and last page matches the original's. Any mismatch → the original is untouched, the temp file is deleted, the user sees a clear message and the unsaved changes stay in the journal.
- **Packing after the rewrite (keeps the file near its original size).** PDFium's full rewrite writes every object uncompressed with a classic xref table, so a book whose structure was in object streams grows by 50–100%. After the plain copy is written and verified, its plain objects are re-packed into Flate object streams plus a cross-reference stream (PDF 1.5+, ISO 32000-1 7.5.7/7.5.8) in a second temp copy; stream objects are copied byte for byte and object numbers are kept; an unreferenced leftover cross-reference stream from a previous save is dropped; any referenced one is kept. The packed copy is verified with the same stroke/annotation verification plus a fidelity comparison with the plain copy. On any problem (unsupported shape, less than 5% gain, exception, verification mismatch) the packed copy is deleted and the plain verified copy is used: the failure mode is "bigger file", never "broken file". Packing is skipped above 300 MB and costs a few seconds of PDFium-thread time on big books (about 4–5 s measured on a 1,076-page book). The original is never modified before verification.
- **Replacing the original.** PDFium keeps the original open, which blocks replacement: close the PDFium handles on that file, replace atomically (`File.Replace` or move with retry for antivirus/indexer locks), reopen. If replacement fails after retries, keep the temp file, tell the user where it is, offer Save as. Never delete the only complete copy.
- **No extra backup copy** next to the original (it would clutter his folders); the guarantee is verify-before-replace.
- **Rendering hides our strokes in the page bitmap.** The render instance removes the app's annotations from its in-memory copy (never saved) so the bitmap shows only the original content and foreign annotations; our strokes come from the ink layer. If an annotation has a newer schema version or unreadable data, it is **not** removed from the render instance: PDFium draws it as a read-only picture and the app tells the user it cannot be edited.
- **Journal format and place.** `%LOCALAPPDATA%\DreamBooks\recovery\<document key>\journal.jsonl`, document key = hash of the full path (case-insensitive). Append-only JSON Lines, one line per completed command (pen-up, end of an erase gesture, undo, redo), not per point: written synchronously to the OS (survives a killed process), then synced to disk (`fsync`) asynchronously on a background thread. Header line: format version, full path, file fingerprint (size, last-write time, hash of the first and last 64 KB and, over 128 KB, of the middle: whole up to 64 KB, else 16 evenly spaced 4 KB blocks; a header without the middle hash, written by an older version, matches on the other fields). Entries: stroke added (full stroke payload, same JSON as the file), strokes erased (removed Ids and the replacement pieces, each with its index in the final list and its full payload), view state (page, zoom, scroll position, written when it settles, debounced). Undo and redo are journalled as their EFFECT (the inverse or forward add/erase entry), never as symbolic commands, so a replay stays correct even when a command from before the last save is undone. The journal grows with undo/redo churn (every effect carries full stroke payloads) and has no compaction in phase 2: a save truncates it. A torn last line is ignored. The journal is created at the first modification; on a successful save it is rewritten atomically as a fresh header followed by catch-up entries for the changes made while the save ran (they are not in the file; Ruling 17), so it is bare only when nothing is unsaved; it is deleted on "Don't save" and on a clean close (nothing unsaved), and kept on any abnormal end. If a journal write fails, the next command rewrites the whole journal from the document's state (header + catch-up) and a warning shows until that succeeds.
- **Restore.** At startup, if journals exist, the most recent one's document is reopened automatically with its changes replayed through the normal commands (each journal effect becomes one ordinary undo step; the exact previous undo history is not reproduced, the strokes are), marked modified, with a one-line Italian notice. Opening any file whose journal exists does the same. There is no separate restore dialog: to drop the changes, close the document and answer "Don't save". Other journals stay until their documents are opened.
- **Journal does not match the file** (fingerprint differs): do not apply silently; ask with two choices, "apply anyway" (additions apply; erasures of Ids that no longer exist are skipped and counted) and "discard", plus cancel (keeps the journal). If the file no longer exists at its path: a message says so, the journal is kept, discard is offered.
- **One undo history per document** (not per page), capacity unchanged (500). Undoing an operation on another page navigates to that page. (Builder may propose something simpler if it is clearly better.)
- **Page changes no longer ask to discard strokes** (nothing is lost now); changing document with unsaved changes asks Save / Don't save / Cancel like closing.
- **Panel additions** (spartan, Italian from resx): Salva, Salva con nome, a "modified" mark and the file name, a saving indicator, error messages. Strings in `Strings.resx` only.
- **Coordinates.** The model stays top-left, y down, in page points at rotation 0 of the visible (CropBox) page as PDFium renders it. The Pdf layer converts to and from PDF user space including page `/Rotate` and non-zero MediaBox/CropBox origins (`FPDF_DeviceToPage` / `FPDF_PageToDevice` are the likely helpers). Tests with a rotated page and an offset box are mandatory.
- **Position restore** covers page, zoom and scroll offsets from the journal only (FR-04 for saved documents stays a later feature).
- **Strokes load lazily, page by page.** A page's app annotations are read the first time the page is shown (or when the journal replay needs it); opening a 1,000-page e-book must not scan every page (NFR-02: first page within 1 s). The document model records which Ids were loaded at open for each loaded page. Unvisited pages cannot have changed, so a save touches only pages with added or removed strokes (the rewrite still writes the whole file).
- **The saver never deletes an Id it did not load.** The model-vs-file diff removes only app annotations whose Id was loaded from the file in this session and is no longer in the model. Strokes that appeared in the file by other means are left alone.
- **File changed on disk since it was opened.** The document keeps the fingerprint taken at open (size, last-write time, hash of the first and last 64 KB and a sampled hash of the middle, see the journal header above; a change that keeps size and last-write time and falls between two sampled blocks is not seen). Save recomputes it; if it differs, the app does not overwrite silently: it asks "Salva con nome / Sovrascrivi comunque / Annulla". Save as to a new path needs no such check.
- **Save as.** After a successful Save as, the session belongs to the new file: the journal is re-keyed to the new path with a fresh header and fingerprint, "modified" resets, and the old path's journal is deleted (the old file stays as it was; the changes live in the new one). If Save as fails or is cancelled, the session and its journal stay on the old path. After a normal Save the journal header fingerprint is rewritten as the journal is truncated.
- **Single writer per document.** The app holds a lock file `<recovery root>\<document key>\lock` open with `FileShare.None` (delete on close) while a document is open; the OS releases it when the process ends, crash included, and unlike a named mutex it has no thread affinity; a second process opening the same document gets a message and does not touch its journal. Different documents may be open in different processes.
- **Encrypted PDFs and restrictions.** PDFs that need a password keep failing to open (phase 1 message). A PDF that opens without a password but carries an encryption dictionary or restriction flags is **never rewritten** in this phase: the user is told when the document is opened, Save and Save as are refused with an Italian explanation, and the strokes stay in the recovery journal until the user closes the document answering "Don't save" (they just cannot be written into that file yet). Fausto has no protected PDFs to test with, so this is a conservative rule; detection uses PDFium's security-handler and permissions queries and is tested with a synthetic encrypted fixture if one can be built, otherwise by a unit test of the decision function.
- **Fidelity gate.** The first Pdf task of the plan is a go/no-go check of the full rewrite on Fausto's real documents, which live in `C:\Users\Favsto\workspace\temp` (outside the repository, 4 files: two small work documents, one medium document and one e-book of about 4 MB). Tests only ever read copies of them in a temporary folder and never modify the originals, and nothing from them is committed. Checks: page count, text extraction, link count, outline count, form fields, images, metadata, and a visual comparison of rendered pages before and after. If a file fails the checks at save time (the verification step above includes a cheap subset), the original is left untouched and the user gets Save as. If failures are common on his real files, the library choice is reopened with Fausto before anything else is built on it. The e-book is small for a "long" one, so the plan also generates a large synthetic PDF (hundreds of pages) for open-time and save-time measurements.
- **Data model fields.** `InkPoint` already has `TiltX`, `TiltY`, `TipAngle` and `TimestampMs`, and `InkStroke` has a stable `Id`: the JSON includes all of them (`tx`, `ty`, `ta`, `t`; unreported values are omitted, never written as 0). No model change is needed for the schema.
- **Undo capacity stays 500** (FR-62 says at least 200; no change to the requirement text).

## 6. Constraints

- `DreamBooks.Core` stays free of WPF, PDFium and PDFsharp: the document model, the journal format and replay, the model-vs-file diff and the schema (JSON) live in Core; everything PDFium lives in `DreamBooks.Pdf`; UI in App.
- All PDFium calls (render, load, annotate, save, verify) on the single PDFium thread. Journal durability: each completed command is **appended synchronously** on the command path (a plain write of a few KB, well under a millisecond, so it cannot disturb the pen) and the `fsync` (`FlushToDisk`) happens right after on a background thread, batched. A killed process then loses only the stroke being drawn; a power loss or OS crash loses at most the writes not yet synced (milliseconds). The stroke in progress is not journalled: the journal records completed commands.
- App-private data carries a schema version (`"v":1`); a reader meeting a higher version must degrade as described in section 5.
- Never log document content, paths of strokes' points or PDF text (NFR-12). No network (NFR-40).
- No new NuGet packages are needed. PDFsharp is not added.
- Dev machine: 8 GB RAM, little disk: the full rewrite of a large e-book must stream to disk (no holding the whole file in memory beyond PDFium's own); the verification step must not double memory.

## 7. Testing

- TDD in Core: journal write/replay round trip including a torn last line and a header mismatch; model-vs-file diff; JSON schema parse (unknown version, missing fields, corrupt); document model across pages; undo/redo across pages; erase of loaded strokes recorded and replayed.
- TDD in Pdf: write then reopen round trip for every property (the testing rule: every change to the writer or stroke model needs save → reopen → compare strokes, other annotations and page content); original text/link/outline/page counts preserved after a full rewrite on small fixture PDFs (classic xref, xref stream, page with an existing Annots array, page with an existing annotation, rotated page, offset MediaBox, a page with an image); foreign annotation untouched; failure injection (verification mismatch, write error, replace blocked) leaves the original byte-identical; no temp file left; file size after save does not grow with repeated saves (save 10 times, size stable).
- View models: dirty state, Save/Save as command flows, close dialog, journal lifecycle (created, truncated on save, deleted on "Don't save", kept on abnormal end), restore flows.
- Manual checklist for Fausto on the Surface (citing requirement IDs): save then open the result in other readers; kill the app with Task Manager mid-stroke and restart; remove the battery equivalent (hard power-off is not required); save repeatedly and watch the file size; open an already annotated file, erase old strokes, crash, restart; a large e-book; a PDF with a signature (warning appears).

## 8. Left to the builder

Names and class layout (`DocumentSession`, `IDocumentSaver`, journal writer), the exact JSON field names beyond those listed, the fingerprint hash details, the retry policy for the replacement, the Italian wording of messages, the synthetic test fixtures (Fausto's real PDFs are used only for the fidelity gate and the manual checklist), the task breakdown, and how undo across pages navigates.

## 9. Requirement changes to make in the same commit as this design

- Section 2 table, row "Scrittura PDF": PDFsharp → PDFium (same engine as the reader); a note that bookmark writing (phase 3) is decided later.
- FR-84: replace "autosave every 60 s into a recovery file" with the journal described here (never touches the original file).
- FR-86: recovery is automatic on reopening, with a notice (not a proposal dialog).
- Section 10 acceptance criterion about recovering "the last 60 s": replaced by "after a forced close, the restart shows every completed stroke and erase and the same position".
- Section 10 phase 2 line: mention the journal and that the strokes of all pages survive page changes.
- Section 9 architecture / data model: no change (Document already holds pages and strokes).

## 10. Done means

Build with no new warnings, all tests green, Fausto's manual checklist run on the Surface, the requirement changes above committed with the decision, and his approval to merge into `main`.
