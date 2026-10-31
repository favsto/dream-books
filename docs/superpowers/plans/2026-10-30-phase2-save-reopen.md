# Phase 2 Save, Reopen and Crash Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fausto saves his pen strokes into the PDF (visible in any reader, editable again in this app), never loses unsaved work to a crash, and never risks his original file.

**Architecture:** `DreamBooks.Core` gets a document-wide model (`DocumentSession`: lazy pages, baseline, dirty tracking, one undo history), the stroke JSON schema and the journal (entries, codec, recorder, replayer). `DreamBooks.Pdf` (now referencing Core) writes and reads strokes as PDFium ink annotations, saves by full rewrite from a separate PDFium instance to a verified temporary file, and stores journals on disk. `DreamBooks.App` orchestrates save, close, recovery and the Italian UI.

**Tech Stack:** unchanged (.NET 10, WPF, PDFium via `bblanchon.PDFium.Win32` 158.0.8125, CommunityToolkit.Mvvm 8.4.2, xUnit, `System.Text.Json` from the framework). **No new NuGet packages.**

**Spec:** `docs/superpowers/specs/2026-10-30-phase2-save-reopen-design.md`, with the PDFium findings in `docs/superpowers/specs/2026-10-30-phase2-pdfium-write-spike-notes.md` (call order, units, pitfalls: read it before touching PDFium). Fausto's real PDFs for the fidelity gate are in `C:\Users\Favsto\workspace\temp` (outside the repo, never modified, never committed).

## Global Constraints

- Core never references WPF, PDFium or PDFsharp. Pdf gains a `ProjectReference` to Core (Core must never reference Pdf). All PDFium calls (open, render, annotate, save, verify) run on the single `PdfiumThread`.
- Every model change goes through `IUndoableCommand` / `UndoManager`. Stroke coordinates stay in page points, top-left origin, y down, for the visible (CropBox) page at rotation 0; conversion to PDF user space happens only in `DreamBooks.Pdf`.
- Stroke payload JSON: ASCII-only (non-ASCII escaped as `\uXXXX`), carries `"v":1`; unreported device values are omitted, never written as 0. A reader meeting a higher version degrades to read-only (the annotation stays a PDFium-drawn picture, never edited or deleted by the app).
- Saving = full rewrite (`FPDF_NO_INCREMENTAL`) from a **separate PDFium instance used only for saving**, to a temporary file in the same folder, verified, then replaced. The original is never modified before verification passes; no stray temp file remains after any outcome; unsaved changes stay in the journal on any failure.
- The saver deletes only app annotations whose Id the session loaded and the model no longer has; it never touches foreign annotations or Ids it did not load.
- Journal: synchronous append of each completed command, `fsync` right after on a background thread; torn last line tolerated; never written into the original PDF; deleted on "Don't save" and on Save as (old path); truncated on successful Save.
- PDFs that need a password keep failing to open. Encrypted or restricted PDFs are never rewritten: Save and Save as are refused with an Italian message.
- User-visible text only in `Strings.resx` (Italian) with the hand-written `Strings.Designer.cs` kept in sync (the drift test must stay green). Never log document content, stroke points or paths of strokes (NFR-12); exception messages shown to the user never include document content or file paths of other files.
- Undo capacity stays 500. TDD in Core and Pdf; view models unit-tested; nothing here is run as the WPF app by agents; do not stage `.claude/settings.json`; commit trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`; `dotnet build` 0 warnings, `dotnet test` green, `dotnet format --verify-no-changes` clean at the end of every task.
- Tests that touch Fausto's real PDFs (`C:\Users\Favsto\workspace\temp`) only read copies placed in a temporary folder, and skip (not fail) when the folder is missing.

## Review Focus

1. **A crash or failure at any step of saving** (write error, verification mismatch, replace blocked, process killed after the temp file exists): the original stays byte-identical, no temp file is left behind, the journal still holds the changes. Pinned in Tasks 10 and 12 by failure-injection tests.
2. **Journal edge cases**: torn last line, empty journal, header only, journal for a file that changed or disappeared, replay of an erase whose Ids no longer exist, journal after Save as. Pinned in Tasks 8, 9 and 13.
3. **Rotated pages and offset boxes**: a stroke written on a page with `/Rotate` 90/180/270 or a MediaBox/CropBox that does not start at (0,0) must reopen at exactly the same model coordinates. Pinned in Tasks 4 and 5.
4. **Double drawing or lost strokes**: app annotations hidden from the page bitmap yet still saved; an annotation with a newer schema or corrupt payload stays visible, uneditable and is not duplicated or deleted by a save; foreign annotations untouched. Pinned in Tasks 5, 6 and 10.
5. **Big documents**: opening does not scan every page (lazy loading counted in a test), saving 10 times does not grow the file, undo across pages works. Pinned in Tasks 7, 10 and 14.

---

### Task 1: Pdf editing foundation and the fidelity gate

**Files:**
- Modify: `src/DreamBooks.Pdf/DreamBooks.Pdf.csproj` (add `ProjectReference` to Core), `src/DreamBooks.Pdf/Interop/PdfiumNative.cs`
- Create: `src/DreamBooks.Pdf/Interop/PdfiumDocument.cs`, `src/DreamBooks.Pdf/PdfFullRewrite.cs`, `src/DreamBooks.Pdf/PdfFidelity.cs`
- Test: `tests/DreamBooks.Pdf.Tests/PdfFullRewriteTests.cs`, `tests/DreamBooks.Pdf.Tests/FidelityGateTests.cs`

**Interfaces:**
- Produces:
  - `PdfiumNative` additions (all internal, `LibraryImport`): `FPDF_SaveAsCopy`, the `FPDF_FILEWRITE` struct with an `[UnmanagedCallersOnly]` write callback (spike notes: `{int version=1; WriteBlock(self, data, unsigned long size)}`); only file-based document loading is needed. Add the text/link/outline/metadata functions the fidelity comparison needs (`FPDFText_LoadPage`, `FPDFText_CountChars`, `FPDFText_GetText`, `FPDFText_ClosePage`, `FPDFLink_*` or annotation subtype counting, `FPDFBookmark_GetFirstChild/GetNextSibling`, `FPDF_GetMetaText`, `FPDFPage_CountObjects`, `FPDFPageObj_GetType`).
  - `internal sealed class PdfiumDocument : IDisposable` — a document handle owned by the PDFium thread: `static PdfiumDocument Open(string path)` (throws `PdfOpenException`, same error mapping as `PdfRenderer`), `IntPtr Handle`, `IntPtr LoadPage(int pageIndex)` / `void ClosePage(IntPtr page)`, `int PageCount`, `void SaveFullRewrite(string targetPath)` (writes via `FPDF_SaveAsCopy(FPDF_NO_INCREMENTAL)` through the callback to a `FileStream`, flushes to disk), `Dispose`. Must only be used inside `PdfiumThread.RunAsync`.
  - `public static class PdfFullRewrite { public static Task RewriteAsync(string sourcePath, string targetPath, CancellationToken ct = default); }` — opens a fresh instance, rewrites, closes.
  - `internal static class PdfFidelity { internal static FidelityReport Compare(string originalPath, string rewrittenPath); }` and `internal sealed record FidelityReport(IReadOnlyList<string> Differences)` with `bool IsFaithful => Differences.Count == 0`; compares per document: page count and page sizes; per page: extracted text equality, link-annotation count, other-annotation counts by subtype, page-object counts by type (images included); document: outline entry count, metadata fields (Title, Author, Subject, Keywords, Creator, Producer); plus a low-resolution (width 200 px) rendered-pixel difference per page for the first, middle and last page, mean absolute difference per channel ≤ 2/255.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public async Task Rewrite_of_synthetic_pdf_keeps_page_count_sizes_and_text()
[Fact] public async Task Rewrite_does_not_modify_the_source_file()               // SHA-256 before == after
[Fact] public async Task Rewrite_to_a_path_in_a_missing_folder_throws_and_leaves_no_file()
[Fact] public async Task Rewrite_of_garbage_file_throws_PdfOpenException_InvalidFormat()
[Fact] public async Task Fidelity_gate_on_real_documents()                       // reads each *.pdf in C:\Users\Favsto\workspace\temp as a COPY in a temp folder; rewrites; Compare; Assert.True(report.IsFaithful) per file; TestOutput lists the differences and the size before/after; skipped (no failure) if the folder is missing or empty
```

- [ ] **Step 2:** `dotnet test tests/DreamBooks.Pdf.Tests` → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS (the real-document test either passes or reports differences).
- [ ] **Step 5 (gate, controller only):** the controller reads the real-document report. If any real file is not faithful, STOP and report to Fausto before Task 2; do not continue the plan on a failed gate.
- [ ] **Step 6:** Commit `feat: PDFium full rewrite and fidelity comparison on real documents (FR-82, NFR-10)`.

---

### Task 2: Document security and signature information

**Files:**
- Create: `src/DreamBooks.Pdf/PdfDocumentInfo.cs`
- Modify: `src/DreamBooks.Pdf/PdfRenderer.cs`, `src/DreamBooks.Pdf/Interop/PdfiumNative.cs`
- Test: `tests/DreamBooks.Pdf.Tests/PdfDocumentInfoTests.cs`

**Interfaces:**
- Produces: `public sealed record PdfDocumentInfo(bool IsEncrypted, bool HasRestrictions, int SignatureCount)`; `PdfRenderer.Info` (read once at open on the PDFium thread via `FPDF_GetSecurityHandlerRevision`, `FPDF_GetDocPermissions`, `FPDF_GetSignatureCount`); `bool CanBeRewritten => !IsEncrypted && !HasRestrictions` as a property of the record; `internal static PdfDocumentInfo Read(IntPtr documentHandle)` (PDFium thread only), reused by `PdfDocumentSaver` in Task 10 on its own fresh instance.

- [ ] **Step 1: Failing tests**: `Plain_pdf_is_not_encrypted_and_has_no_signatures`; `Decision_function_refuses_encrypted_or_restricted` (unit test of the pure `CanBeRewritten` logic with constructed records); and, only if a hand-built encrypted fixture is feasible in the test helper, `Encrypted_fixture_is_reported_as_encrypted` (otherwise record in the report that no encrypted fixture could be built).
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS. **Step 5:** Commit `feat: report encryption, restrictions and signatures of opened PDFs (FR-82, NFR-11)`.

---

### Task 3: Core contracts and the stroke JSON schema

**Files:**
- Create: `src/DreamBooks.Core/Model/StrokeJson.cs`, `src/DreamBooks.Core/Documents/IPageSource.cs`, `PageDimensions.cs`, `PageStrokes.cs`, `PageChange.cs`, `DocumentFingerprint.cs`
- Test: `tests/DreamBooks.Core.Tests/Model/StrokeJsonTests.cs`, `tests/DreamBooks.Core.Tests/Documents/DocumentFingerprintTests.cs`

**Interfaces:**
- Produces (namespace `DreamBooks.Core.Documents` unless noted):
  - `DreamBooks.Core.Model.StrokeJson`: `const int CurrentVersion = 1`; `string Serialize(InkStroke stroke)` → `{"v":1,"id":"<Guid N>","tool":"pen"|"highlighter","color":"#AARRGGBB","w":<baseWidthPt>,"pts":[{"x":..,"y":..,"p":..,"tx":..,"ty":..,"ta":..,"t":<ms>}...]}` with doubles in round-trip `"R"` format, invariant culture, optional fields omitted when null; `StrokeParseResult Parse(string json)`; `enum StrokeParseStatus { Ok, UnsupportedVersion, Corrupt }`; `readonly record struct StrokeParseResult(StrokeParseStatus Status, InkStroke? Stroke)`.
  - `readonly record struct PageDimensions(double WidthPt, double HeightPt)`.
  - `sealed record PageStrokes(IReadOnlyList<InkStroke> Strokes, int UnreadableCount)`.
  - `interface IPageSource { int PageCount { get; } PageDimensions GetPageDimensions(int pageIndex); Task<PageStrokes> LoadStrokesAsync(int pageIndex, CancellationToken ct = default); }`
  - `sealed record PageChange(int PageIndex, IReadOnlyList<Guid> RemovedIds, IReadOnlyList<InkStroke> Added)`.
  - `readonly record struct DocumentFingerprint(long Length, long LastWriteUtcTicks, string EdgeHash)` with `static Task<DocumentFingerprint> ComputeAsync(string path, CancellationToken ct = default)` (length, last-write UTC ticks, SHA-256 hex of the first and last 64 KB, fewer bytes for small files) and `string ToToken()` / `static bool TryParse(string token, out DocumentFingerprint f)` for the journal header.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void Serialize_then_Parse_round_trips_every_field_of_a_stroke()          // incl. null pressure/tilt/tipangle omitted and restored as null, and a non-zero timestamp
[Fact] public void Serialized_json_is_ascii_only_and_starts_with_version_1()           // plus an internal escaping helper test: a string containing 'è' and '€' becomes è and € and parses back
[Fact] public void Doubles_round_trip_exactly()                                         // 0.1, 1e-7, 123456.789012345
[Fact] public void Parse_unknown_future_version_returns_UnsupportedVersion()           // {"v":2,...}
[Fact] public void Parse_corrupt_or_truncated_json_returns_Corrupt()                   // truncated, wrong types, empty pts, missing id, NaN/Infinity text
[Fact] public void Fingerprint_changes_when_length_or_edge_bytes_or_write_time_change()
[Fact] public void Fingerprint_of_a_file_smaller_than_the_edge_window_works()
[Fact] public void Fingerprint_token_round_trips_and_rejects_garbage()
```

- [ ] **Step 2:** `dotnet test tests/DreamBooks.Core.Tests` → FAIL. **Step 3:** Implement with `System.Text.Json` (`Utf8JsonWriter` with `JavaScriptEncoder.Default`-style ASCII escaping, or manual writer; the output must satisfy the ASCII test). **Step 4:** Run → PASS. **Step 5:** Commit `feat: stroke JSON schema v1 and document contracts (FR-83)`.

---

### Task 4: Page geometry mapping

**Files:**
- Create: `src/DreamBooks.Pdf/PageGeometry.cs`
- Modify: `tests/DreamBooks.Pdf.Tests/TestPdfFactory.cs` (stays dependency-free; it is linked into App.Tests)
- Test: `tests/DreamBooks.Pdf.Tests/PageGeometryTests.cs`

**Interfaces:**
- Produces: `internal sealed class PageGeometry` built from a PDFium page: `static PageGeometry From(IntPtr page)`; `PageDimensions Dimensions` (visible size in points after rotation, as PDFium renders); `PointD ModelToUser(PointD model)` and `PointD UserToModel(PointD user)` (user = PDF default user space, y up, including MediaBox/CropBox origin and `/Rotate`); `(double Left, double Bottom, double Right, double Top) RectToUser(...)` helper for bounding boxes. Implemented with `FPDF_GetPageBoundingBox`/`FPDFPage_GetCropBox`/`GetMediaBox`/`FPDFPage_GetRotation` or `FPDF_PageToDevice`/`FPDF_DeviceToPage` at size = page size at rotation 0, whichever tests prove exact.
- `TestPdfFactory` gains an overload `Write(string path, params PageSpec[] pages)` where `public sealed record PageSpec(double Width, double Height, int Rotate = 0, double OriginX = 0, double OriginY = 0, bool WithForeignSquareAnnotation = false, bool ExistingAnnotsAsIndirectArray = false)`; the existing `Write(path, params (double w,double h)[])` keeps working. Page 1 content: a few lines of Helvetica text and a black 100x100 pt square at the visible top-left.

- [ ] **Step 1: Failing tests**

```csharp
[Theory] [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
public void ModelToUser_then_UserToModel_round_trips(int rotate)                    // 50 random points, ±1e-6
[Fact] public void Unrotated_page_maps_model_top_left_to_user_top_left()            // (0,0)model -> (0,height) user for origin (0,0)
[Fact] public void Offset_MediaBox_origin_is_added()                                // MediaBox [100 200 695 1042]: model (0,0) -> user (100,1042)
[Fact] public void Rotate_90_swaps_visible_dimensions()                             // 595x842 rotate 90 -> Dimensions 842x595
[Fact] public void Rendered_black_square_corner_matches_mapped_corner_for_each_rotation()  // render the factory page with PdfRenderer at 1 px/pt and check the pixel at the model position of the square's corner
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS. **Step 5:** Commit `feat: page geometry mapping for rotated pages and offset boxes (FR-26, FR-81)`.

---

### Task 5: Stroke annotations: write and read

**Files:**
- Create: `src/DreamBooks.Pdf/StrokeAnnotations.cs`
- Modify: `src/DreamBooks.Pdf/Interop/PdfiumNative.cs` (annotation functions from the spike notes)
- Test: `tests/DreamBooks.Pdf.Tests/StrokeAnnotationsTests.cs`

**Interfaces:**
- Consumes: `StrokeJson`, `StrokeGeometry.BuildOutline(stroke, PressureCurve.Firm, SmoothingLevel.Strong)` (the same polygon the app draws), `PageGeometry`, `PdfiumDocument`.
- Produces (internal, called on the PDFium thread): `static class StrokeAnnotations`:
  - `const string PayloadKey = "DRBK_Data"`.
  - `void Write(IntPtr page, PageGeometry geometry, InkStroke stroke)`: creates one ink annotation following the spike call order (CreateAnnot → SetRect → SetColor → SetBorder → AddInkStroke(centreline from the stroke's points, simplified to ≤ ~100 pt) → SetStringValue(`/NM` = Id "N" format, `/M` date, `/T` "Dream Books", `PayloadKey` = `StrokeJson.Serialize`) → SetFlags(4) → SetAP last with the filled contour in user space, `rg` colour from the stroke colour, no alpha) and closes it.
  - `StrokeReadResult Read(IntPtr page, PageGeometry geometry)` → `sealed record StrokeReadResult(IReadOnlyList<InkStroke> Strokes, IReadOnlyList<int> UnreadableAnnotationIndexes, int ForeignCount)`: app annotations are those with `/Subtype /Ink` and the `PayloadKey`; parse status Ok → stroke; UnsupportedVersion or Corrupt → index in `UnreadableAnnotationIndexes`; everything else is foreign.
  - `bool RemoveById(IntPtr page, Guid id)` removes the app annotation whose payload Id matches (also accepts `/NM`); returns false if absent.
  - `int RemoveAllApp(IntPtr page)` removes every app annotation that parsed Ok (used by the render instance, Task 6); returns the count.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public async Task Written_stroke_is_read_back_identical()                                  // every InkPoint field, Id, tool, colour, width, order
[Fact] public async Task Annotation_has_ink_subtype_print_flag_name_and_appearance()               // subtype 15, flags 4, NM == Id, AP present
[Fact] public async Task Rendering_with_annotations_shows_the_stroke_and_without_does_not()        // pixel at a stroke centre dark with FPDF_ANNOT, white without
[Fact] public async Task Pressure_width_is_visible_in_the_render()                                  // thin start vs thick end measured on a rendered row
[Theory][InlineData(0)][InlineData(90)][InlineData(180)][InlineData(270)]
public async Task Stroke_survives_rotation_and_offset_box_at_the_same_model_coordinates(int rotate)  // incl. MediaBox origin (100,200)
[Fact] public async Task Foreign_annotation_is_counted_and_never_returned_as_a_stroke()             // WithForeignSquareAnnotation
[Fact] public async Task Newer_schema_annotation_is_reported_unreadable_and_kept()                  // payload with "v":2 written via a raw SetStringValue
[Fact] public async Task Corrupt_payload_is_reported_unreadable_and_kept()
[Fact] public async Task RemoveById_removes_only_that_annotation_and_keeps_foreign_ones()
[Fact] public async Task Dot_and_one_point_stroke_round_trip()
[Fact] public async Task Existing_indirect_Annots_array_is_preserved()                             // ExistingAnnotsAsIndirectArray
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS. **Step 5:** Commit `feat: write and read strokes as PDF ink annotations with private payload (FR-81, FR-83)`.

---

### Task 6: PdfRenderer loads strokes and hides them from the bitmap

**Files:**
- Modify: `src/DreamBooks.Pdf/PdfRenderer.cs`
- Test: `tests/DreamBooks.Pdf.Tests/PdfRendererStrokesTests.cs`

**Interfaces:**
- Consumes: `StrokeAnnotations`, `PageGeometry`, `IPageSource`.
- Produces: `PdfRenderer : IAsyncDisposable, IPageSource` — `PageDimensions GetPageDimensions(int pageIndex)` (from `PageGeometry`; keep `GetPageSize` working), `Task<PageStrokes> LoadStrokesAsync(int pageIndex, CancellationToken ct = default)`, `string Path { get; }`, `PdfDocumentInfo Info { get; }` (Task 2). Rendering (`RenderPageAsync`) loads the page and **removes the app annotations that parsed Ok from the in-memory render instance before drawing** (never saved), so the bitmap never shows them; unreadable/newer annotations are left so PDFium draws them as pictures; foreign annotations are drawn as before.

- [ ] **Step 1: Failing tests**: `LoadStrokes_returns_the_strokes_written_by_StrokeAnnotations`; `LoadStrokes_reports_unreadable_count`; `Render_does_not_draw_app_strokes_but_draws_foreign_annotations`; `Render_draws_unreadable_annotations_as_pictures`; `LoadStrokes_for_page_out_of_range_throws_ArgumentOutOfRange`; `LoadStrokes_runs_on_the_PDFium_thread`; `Rendering_twice_after_loading_does_not_change_the_file_on_disk`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS. **Step 5:** Commit `feat: load strokes from PDF pages and keep them out of the page bitmap (FR-83, FR-26)`.

---

### Task 7: DocumentSession: document-wide model

**Files:**
- Create: `src/DreamBooks.Core/Documents/DocumentSession.cs`, `src/DreamBooks.Core/Undo/CommandAction.cs`
- Modify: `src/DreamBooks.Core/Undo/UndoManager.cs`, `src/DreamBooks.Core/Undo/IUndoableCommand.cs` (no change to the interface; add `IPageCommand` in `src/DreamBooks.Core/Commands/IPageCommand.cs`), `AddStrokesCommand.cs`, `EraseStrokesCommand.cs`
- Test: `tests/DreamBooks.Core.Tests/Documents/DocumentSessionTests.cs`, extend `tests/DreamBooks.Core.Tests/Undo/UndoManagerTests.cs`, `tests/DreamBooks.Core.Tests/Commands/StrokeCommandsTests.cs`

**Interfaces:**
- Produces:
  - `enum CommandAction { Executed, Undone, Redone }`; `UndoManager`: `event EventHandler<CommandAppliedEventArgs>? CommandApplied` (args: `CommandAction Action`, `IUndoableCommand Command`, raised after `Execute`/`Undo`/`Redo` succeed, before `StateChanged`), `IUndoableCommand? PeekUndo()`, `IUndoableCommand? PeekRedo()`.
  - `interface IPageCommand : IUndoableCommand { int PageIndex { get; } }` implemented by `AddStrokesCommand` and `EraseStrokesCommand` (`PageIndex => page.Index`); `AddStrokesCommand.Strokes` and `EraseStrokesCommand.Before/After` exposed as `IReadOnlyList<InkStroke>` for the journal.
  - `public sealed class DocumentSession`: ctor `DocumentSession(IPageSource source, string path, DocumentFingerprint openFingerprint, UndoManager? undo = null)`; `string Path`, `DocumentFingerprint OpenFingerprint`, `int PageCount`, `UndoManager Undo`; `Task<Page> EnsurePageAsync(int pageIndex, CancellationToken ct = default)` (loads strokes once via `source.LoadStrokesAsync`, creates the `Page` with `GetPageDimensions`, stores the baseline Id sequence, never reloads); `bool IsPageLoaded(int)`; `int CurrentPageIndex`, `PageEditor? CurrentEditor`, `event EventHandler? CurrentPageChanged`, `Task GoToPageAsync(int pageIndex, CancellationToken ct = default)` (does **not** clear undo; in-flight erase handling stays in the VM); `bool IsDirty`, `event EventHandler? DirtyChanged` (dirty = some loaded page's current stroke Id sequence differs from its baseline; recomputed after every command, undo/redo and `MarkSaved`); `int UnreadableAnnotationCount`; `IReadOnlyList<PageChange> GetChanges()` (per changed loaded page: `RemovedIds` = baseline Ids missing now, `Added` = current strokes whose Id is not in the baseline, in current order); `void MarkSaved(string? newPath = null, DocumentFingerprint? newFingerprint = null)` (baseline := current for every loaded page; when a new path is given, `Path` changes); `Task<bool> UndoAsync(CancellationToken ct = default)` / `RedoAsync(...)`: peek the command, if it is an `IPageCommand` for another page then `GoToPageAsync` first, then call `Undo.Undo()`/`Redo()`.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public async Task Pages_load_lazily_and_only_once()                              // fake IPageSource counts LoadStrokesAsync calls
[Fact] public async Task Opening_a_thousand_page_document_loads_no_strokes_until_a_page_is_shown()
[Fact] public async Task Strokes_survive_changing_page_and_coming_back()
[Fact] public async Task Undo_history_is_shared_across_pages_and_undo_navigates_to_the_command_page()
[Fact] public async Task IsDirty_is_false_after_open_true_after_commit_false_after_undo_back_to_baseline()
[Fact] public async Task IsDirty_is_false_again_after_MarkSaved_and_true_after_next_change()
[Fact] public async Task GetChanges_lists_added_strokes_and_removed_baseline_ids_only()
[Fact] public async Task Erasing_a_loaded_stroke_reports_its_id_as_removed_and_the_pieces_as_added()
[Fact] public async Task GetChanges_never_reports_strokes_of_unloaded_pages()
[Fact] public async Task MarkSaved_with_a_new_path_rekeys_the_session()
[Fact] public void UndoManager_raises_CommandApplied_for_execute_undo_and_redo_with_the_command()
[Fact] public void UndoManager_PeekUndo_and_PeekRedo_return_the_next_command_or_null()
[Fact] public void Stroke_commands_expose_their_page_index_and_data()
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS. **Step 5:** Commit `feat: document session with lazy pages, dirty tracking and shared undo (FR-62, FR-83, NFR-02)`.

---

### Task 8: Journal: entries, codec, recorder, replayer

**Files:**
- Create: `src/DreamBooks.Core/Journal/JournalEntry.cs` (and one file per public type: `AddEntry`, `EraseEntry`, `ViewEntry`, `IndexedStroke`), `JournalHeader.cs`, `JournalCodec.cs`, `JournalContents.cs`, `IJournalSink.cs`, `JournalRecorder.cs`, `JournalReplayer.cs`, `ReplayResult.cs`
- Test: `tests/DreamBooks.Core.Tests/Journal/JournalCodecTests.cs`, `JournalRecorderTests.cs`, `JournalReplayerTests.cs`

**Interfaces:**
- Consumes: `StrokeJson`, `DocumentSession`, `UndoManager.CommandApplied`, `PeekUndo`.
- Produces (namespace `DreamBooks.Core.Journal`): one JSON object per line.
  - `abstract record JournalEntry`; `AddEntry(int PageIndex, IReadOnlyList<InkStroke> Strokes)`; `EraseEntry(int PageIndex, IReadOnlyList<Guid> RemovedIds, IReadOnlyList<IndexedStroke> Added)` with `IndexedStroke(int Index, InkStroke Stroke)` where `Index` is the stroke's index in the page's final list; `ViewEntry(int PageIndex, double Zoom, double ScrollX, double ScrollY)`.
  - `JournalHeader(int FormatVersion, string Path, DocumentFingerprint Fingerprint)`.
  - `static class JournalCodec`: `string SerializeHeader(JournalHeader)`, `string SerializeEntry(JournalEntry)` (lines have no newline inside; strokes embed `StrokeJson` payloads; ASCII only), `JournalHeader? ParseHeader(string line)`, `JournalEntry? ParseEntry(string line)` (null for a corrupt or torn line), `JournalContents Parse(IEnumerable<string> lines)` → `record JournalContents(JournalHeader? Header, IReadOnlyList<JournalEntry> Entries, bool HadTornTail)` (an unparseable last line sets `HadTornTail` and is dropped; an unparseable line in the middle is skipped and counted: add `int SkippedCount`).
  - `interface IJournalSink { void Append(string line); }` (called on the command path; synchronous).
  - `sealed class JournalRecorder : IDisposable`: ctor `(DocumentSession session, IJournalSink sink)`; subscribes to `session.Undo.CommandApplied`; on `Executed` with an `AddStrokesCommand` → `AddEntry`; with an `EraseStrokesCommand` → `EraseEntry` (removed = ids in `Before` not in `After`; added = strokes in `After` whose Id is not in `Before`, with their index in `After`); `Undone` → the inverse effect (undone Add → `EraseEntry` removing its stroke Ids; undone Erase → `EraseEntry` removing the pieces and re-adding the original strokes with their index in `Before`); `Redone` → the same entry as `Executed` (RULING 10: effects only, never symbolic undo); `void RecordView(int pageIndex, double zoom, double scrollX, double scrollY)` (debounce is the caller's job); stops on `Dispose`.
  - `sealed class JournalReplayer`: `Task<ReplayResult> ReplayAsync(DocumentSession session, JournalContents contents, CancellationToken ct = default)` replays through the normal commands on `session.Undo` (each effect is one ordinary undo step): `AddEntry` → `EnsurePageAsync` then `PageEditor`-less `AddStrokesCommand` executed on the page; `EraseEntry` → remove the listed Ids that exist, insert the added strokes at their recorded indexes (clamped); entries that reference missing Ids are skipped and counted; `ViewEntry` → remembered. `record ReplayResult(int Applied, int Skipped, ViewEntry? LastView)`. The recorder must be detached while replaying (the caller attaches it afterwards) so replay does not re-journal.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void Every_entry_type_round_trips_through_the_codec()
[Fact] public void Entry_lines_are_single_line_ascii()
[Fact] public void Torn_last_line_is_dropped_and_flagged()
[Fact] public void Corrupt_middle_line_is_skipped_and_counted()
[Fact] public void Empty_input_and_header_only_input_parse_to_no_entries()
[Fact] public void Header_with_unknown_format_version_is_reported()
[Fact] public async Task Recorder_writes_add_erase_undo_redo_entries_for_real_commands()
[Fact] public async Task Recorder_erase_entry_contains_only_removed_ids_and_new_pieces_with_final_indexes()
[Fact] public async Task Replay_rebuilds_the_same_strokes()         // session A (recorded) vs session B (replayed over the same baseline): same stroke Ids per page; undoing everything on B returns it to baseline and not dirty
[Fact] public async Task Replay_of_erase_of_a_baseline_stroke_removes_it()
[Fact] public async Task Replay_skips_erase_of_missing_ids_and_counts_them()
[Fact] public async Task Replay_applies_across_pages_and_leaves_unvisited_pages_unloaded()
[Fact] public async Task Replay_does_not_write_new_journal_lines()
[Fact] public async Task Last_view_entry_wins()
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS. **Step 5:** Commit `feat: journal of unsaved changes with replay through undo (FR-84, FR-86, FR-63)`.

---

### Task 9: RecoveryStore: journals on disk

**Files:**
- Create: `src/DreamBooks.Pdf/RecoveryStore.cs`, `src/DreamBooks.Pdf/RecoveredJournal.cs`
- Test: `tests/DreamBooks.Pdf.Tests/RecoveryStoreTests.cs`

**Interfaces:**
- Consumes: `IJournalSink`, `JournalCodec`, `JournalHeader`.
- Produces: `public sealed class RecoveryStore`: ctor `RecoveryStore(string rootDirectory)` (the app passes `%LOCALAPPDATA%\DreamBooks\recovery`); `static string DocumentKey(string fullPath)` (lowercase hex SHA-256 of the full path upper-cased invariant, 32 chars); `IJournalSession Begin(string documentPath, DocumentFingerprint fingerprint)`: creates `<root>\<key>\journal.jsonl` with the header line if missing (an existing file is appended to; header already present is kept); `public interface IJournalSession : IJournalSink, IAsyncDisposable { void Truncate(JournalHeader newHeader); void Delete(); string FilePath { get; } }` — `Append` writes the line + `\n` with a synchronous write to a `FileStream` opened `FileShare.Read`, then queues an `fsync` (`FlushToDisk`) on a single background worker (coalesced); `Truncate` rewrites to the new header only (used after Save and re-key after Save as); `Delete` removes the journal and its folder; `IReadOnlyList<RecoveredJournal> FindAll()` → `record RecoveredJournal(string FilePath, JournalContents Contents, DateTime LastWriteUtc)` sorted most recent first (journals with an unreadable header are returned with `Header == null` so the app can ignore or delete them); `RecoveredJournal? Find(string documentPath)`; `void Delete(string documentPath)`.

- [ ] **Step 1: Failing tests** (use a temp directory per test)

```csharp
[Fact] public void DocumentKey_is_stable_case_insensitive_and_differs_per_path()
[Fact] public void Begin_creates_a_journal_with_the_header_line()
[Fact] public void Appended_lines_are_readable_by_a_second_reader_immediately()
[Fact] public void Truncate_leaves_only_the_new_header()
[Fact] public void Delete_removes_file_and_folder_and_is_idempotent()
[Fact] public async Task Dispose_flushes_pending_fsync_and_does_not_throw_twice()
[Fact] public void FindAll_returns_most_recent_first_and_flags_unreadable_headers()
[Fact] public void A_file_with_a_torn_last_line_is_read_back_without_it()              // write half a line directly, then FindAll
[Fact] public void Append_after_Truncate_continues_the_new_journal()
[Fact] public void Journal_files_never_appear_next_to_the_document()                    // directory of the document unchanged
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS. **Step 5:** Commit `feat: recovery store for journals under LocalAppData (FR-84)`.

---

### Task 10: Saver: verified copy and safe replacement

**Files:**
- Create: `src/DreamBooks.Pdf/PdfDocumentSaver.cs`, `src/DreamBooks.Pdf/FileReplacer.cs`, `src/DreamBooks.Pdf/SaveVerificationException.cs`
- Modify: `src/DreamBooks.Core/Documents/IDocumentWriter.cs` (create in Core)
- Test: `tests/DreamBooks.Pdf.Tests/PdfDocumentSaverTests.cs`, `tests/DreamBooks.Pdf.Tests/FileReplacerTests.cs`

**Interfaces:**
- Produces:
  - Core: `interface IDocumentWriter { Task<string> WriteVerifiedCopyAsync(string sourcePath, string destinationPath, IReadOnlyList<PageChange> changes, CancellationToken ct = default); }` returning the path of the verified temporary file (a sibling of `destinationPath`, named `<destination file name>.<8 hex>.drbk-tmp`); never modifies `sourcePath` or `destinationPath`.
  - Pdf: `public sealed class PdfDocumentSaver : IDocumentWriter`. Algorithm: on the PDFium thread open a **fresh** `PdfiumDocument` from `sourcePath`; refuse (throw `PdfSaveRefusedException`) if the document is encrypted/restricted; for each change load only that page, `RemoveById` each removed Id, `Write` each added stroke; `SaveFullRewrite` to the temp file; **verify**: reopen the temp file and check page count equals the source's; per changed page the app annotation count equals (baseline app count − removed + added) and every payload parses and equals what `StrokeJson.Serialize` produced for the added strokes; foreign annotation count per changed page unchanged; page sizes unchanged; extracted text of the first and last page equals the source's. Any failure → delete the temp file and throw `SaveVerificationException` (message without document content).
  - Pdf: `public static class FileReplacer { public static void Replace(string tempPath, string targetPath, int attempts = 5, TimeSpan? delay = null); }` — `File.Replace`-style atomic replacement keeping the target intact on failure, retrying on `IOException`/`UnauthorizedAccessException` with growing delay; on final failure throws `ReplaceFailedException` and **keeps the temp file** (the caller tells the user where it is).
  - Pdf: `public sealed class PdfSaveRefusedException : Exception`.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public async Task Saved_copy_contains_added_strokes_and_not_removed_ones()
[Fact] public async Task Saved_copy_keeps_text_links_page_count_and_foreign_annotations()
[Fact] public async Task Source_file_is_byte_identical_after_writing_the_copy()
[Fact] public async Task Writing_with_no_changes_still_produces_a_valid_equal_copy()
[Fact] public async Task Ids_not_loaded_by_the_session_are_never_removed()               // removed id absent in file -> ignored, others untouched
[Fact] public async Task Newer_schema_and_corrupt_annotations_survive_a_save_untouched()
[Fact] public async Task Verification_mismatch_deletes_the_temp_file_and_throws()        // inject via an internal hook that corrupts the written copy
[Fact] public async Task Write_error_deletes_the_temp_file_and_leaves_the_source_unchanged()   // target folder read-only / disk-full simulation through an injectable stream factory
[Fact] public async Task Cancellation_deletes_the_temp_file()
[Fact] public async Task Repeated_saves_do_not_grow_the_file()                             // save 10 times with a stable stroke set: size within ±1% of the second save
[Fact] public async Task Saving_a_rotated_offset_page_round_trips_strokes()
[Fact] public async Task Encrypted_or_restricted_document_is_refused()                     // via the internal decision seam from Task 2 if no fixture
[Fact] public void Replace_swaps_the_target_and_removes_the_temp()
[Fact] public void Replace_retries_when_the_target_is_briefly_locked_and_then_succeeds()
[Fact] public void Replace_failure_leaves_the_target_byte_identical_and_keeps_the_temp()
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS. **Step 5:** Commit `feat: verified full-rewrite save with safe file replacement (FR-80, FR-82, NFR-10)`.

---

### Task 11: App on the document session

**Files:**
- Modify: `src/DreamBooks.App/ViewModels/MainViewModel.cs`, `src/DreamBooks.App/Views/PageView.xaml.cs` and `src/DreamBooks.App/Ink/InkSurface.cs` only where they read `vm.Editor`, `PageChanged`, `PageSize`
- Modify: `src/DreamBooks.App/Services/IFileDialogService.cs` (remove `Confirm`, now unused) and `FileDialogService.cs`
- Test: `tests/DreamBooks.App.Tests/MainViewModelTests.cs` (adapt), `tests/DreamBooks.App.Tests/DocumentNavigationTests.cs`

**Interfaces:**
- Consumes: `DocumentSession`, `PdfRenderer : IPageSource`.
- Produces: `MainViewModel` keeps its public members (`Editor`, `PageSize`, `PageNumber`, `PageCount`, `PageChanged`, `Undo`, `ErrorMessage`, the commands and the eraser API) but owns `public DocumentSession? Session`; `OpenPdfAsync` builds `new DocumentSession(renderer, path, await DocumentFingerprint.ComputeAsync(path))` after the renderer opens and `Info`-independent checks; `GoToPageAsync` calls `Session.GoToPageAsync` (cancelling any live erase gesture first) and **no longer asks to discard strokes nor clears undo**; `UndoCommand`/`RedoCommand` call `Session.UndoAsync()`/`RedoAsync()`; `PageSize`/`PageNumber` follow `Session.CurrentPageIndex`; opening a second document while `Session.IsDirty` is handled in Task 12 (until then it replaces the session as before). `bool HasUnreadableAnnotations` + Italian notice `Strings.NoticeUnreadableAnnotations` when `Session.UnreadableAnnotationCount > 0` after a page loads. Remove `ConfirmDiscard` and the `DiscardStrokesConfirm` string.

- [ ] **Step 1: Failing tests**: `Strokes_drawn_on_page_1_are_still_there_after_visiting_page_2`; `Page_change_does_not_clear_undo_history`; `Undo_of_a_stroke_on_another_page_navigates_to_it`; `Opening_a_document_does_not_load_strokes_of_other_pages` (fake `IPageSource` seam via the existing `OpenRenderer` seam extended to produce a session factory); `Erase_in_progress_is_cancelled_on_page_change`; `Unreadable_annotations_raise_the_notice_once_per_document`; the existing view-model tests updated for the new flow.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement (keep the eraser preview and zoom lock behaviour unchanged). **Step 4:** Run → PASS; full suite green. **Step 5:** Commit `refactor: main view model on the document session, strokes survive page changes (FR-26, FR-62, FR-83)`.

---

### Task 12: Save, Save as, close and the saving pipeline

**Files:**
- Create: `src/DreamBooks.App/Services/DocumentSaveService.cs`, `src/DreamBooks.App/Services/SaveOutcome.cs`
- Modify: `src/DreamBooks.App/ViewModels/MainViewModel.cs`, `src/DreamBooks.App/Services/IFileDialogService.cs` and `FileDialogService.cs`, `src/DreamBooks.App/MainWindow.xaml(.cs)`, `src/DreamBooks.App/Resources/Strings.resx` + `Strings.Designer.cs`
- Test: `tests/DreamBooks.App.Tests/DocumentSaveServiceTests.cs`, `tests/DreamBooks.App.Tests/SaveCommandsTests.cs`

**Interfaces:**
- Consumes: `IDocumentWriter`, `FileReplacer`, `DocumentFingerprint`, `PdfRenderer.Info`, `DocumentSession`.
- Produces:
  - `IFileDialogService` additions: `string? PickSavePdf(string suggestedFileName)`, `SaveChangesAnswer AskSaveChanges()` (`enum SaveChangesAnswer { Save, DontSave, Cancel }`), `FileChangedAnswer AskFileChanged()` (`enum { SaveAs, OverwriteAnyway, Cancel }`), `bool ConfirmSignatureInvalidation()`, `void ShowMessage(string text)`.
  - `enum SaveOutcome { Saved, Cancelled, Refused, Failed }`.
  - `DocumentSaveService(IDocumentWriter writer, IFileDialogService dialogs, Func<PdfRenderer, Task> releaseRenderer, Func<string, CancellationToken, Task<PdfRenderer>> reopenRenderer)`; `Task<SaveOutcome> SaveAsync(DocumentSession session, PdfRenderer renderer, bool saveAs, CancellationToken ct)`. Pipeline: refuse if `!renderer.Info.CanBeRewritten` (message `Strings.ErrorSaveEncrypted`); warn when `SignatureCount > 0` (`ConfirmSignatureInvalidation`, declining → Cancelled); for plain Save recompute the fingerprint and if it differs from `session.OpenFingerprint` ask `AskFileChanged` (SaveAs → continue as Save as; Cancel → Cancelled); target path = session path or `PickSavePdf`; `writer.WriteVerifiedCopyAsync(session.Path, target, session.GetChanges())` (for Save as the **source** is still the opened file and the target is the new path: the writer copies the whole file content through the rewrite, so the temp sits next to the target); then `releaseRenderer`, `FileReplacer.Replace(temp, target)` (a Save as to a new path just moves the temp into place), `reopenRenderer(target)`, `session.MarkSaved(target-if-changed, newFingerprint)`; on any failure after the temp exists the original/target is untouched and the user sees an Italian message; on `ReplaceFailedException` the message includes the temp file's location.
  - `MainViewModel`: `SaveCommand`, `SaveAsCommand` (disabled without a document or while saving), `bool IsSaving`, `bool IsDirty`, `string WindowTitle` ("<file name> *" when dirty, resx for the app name); `Task<bool> CanCloseAsync()` implementing FR-85 (Save → save then close if saved; DontSave → close and, via an event `DiscardRequested` consumed in Task 13, delete the journal; Cancel → stay); `Ctrl+S` / `Ctrl+Shift+S` key bindings; opening another file while dirty asks the same Save / Don't save / Cancel question. MainWindow handles `Closing` by awaiting `CanCloseAsync` (cancel the event first, close again when allowed). Panel: Salva and Salva con nome buttons, a saving indicator (indeterminate bar), the dirty mark in the title. New Italian strings: `Save`, `SaveAs`, `Saving`, `ErrorSaveEncrypted`, `ErrorSaveVerification`, `ErrorSaveFailed`, `ErrorSaveReplaceFailed`, `AskSaveChanges`, `AskFileChanged`, `WarnSignatureInvalidation`, `NoticeUnreadableAnnotations` (no popup on a successful save).

- [ ] **Step 1: Failing tests** (fakes for writer, dialogs and renderer hooks)

```csharp
[Fact] public async Task Save_writes_replaces_reopens_and_marks_the_session_clean()
[Fact] public async Task Original_is_untouched_when_the_writer_fails()                 // temp removed by the writer; journal untouched (asserted in Task 13)
[Fact] public async Task Replace_failure_reports_the_temp_location_and_keeps_the_session_dirty()
[Fact] public async Task Encrypted_document_is_refused_with_the_italian_message_and_nothing_is_written()
[Fact] public async Task Signed_document_asks_confirmation_and_declining_cancels()
[Fact] public async Task Changed_fingerprint_asks_and_Cancel_does_nothing()
[Fact] public async Task Changed_fingerprint_SaveAs_switches_to_a_picked_path()
[Fact] public async Task Save_as_moves_the_session_to_the_new_path_and_keeps_the_old_file_untouched()
[Fact] public async Task Save_as_dialog_cancelled_returns_Cancelled_without_writing()
[Fact] public async Task Renderer_is_released_before_replace_and_reopened_after()
[Fact] public async Task CanClose_without_changes_returns_true_without_asking()
[Fact] public async Task CanClose_with_changes_Save_then_true_only_if_saved()
[Fact] public async Task CanClose_DontSave_returns_true_and_raises_DiscardRequested()
[Fact] public async Task CanClose_Cancel_returns_false()
[Fact] public async Task Save_buttons_are_disabled_while_saving_and_without_a_document()
[Fact] public async Task WindowTitle_shows_the_dirty_mark()
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS; `dotnet format`. **Step 5:** Commit `feat: save, save as and close with confirmation, verified replacement pipeline (FR-80, FR-85, FR-89, NFR-10)`.

---

### Task 13: Journal wiring, crash recovery and single writer

**Files:**
- Create: `src/DreamBooks.App/Services/RecoveryService.cs`, `src/DreamBooks.App/Services/DocumentLock.cs`
- Modify: `src/DreamBooks.App/ViewModels/MainViewModel.cs`, `src/DreamBooks.App/Views/PageView.xaml.cs` (report view state), `src/DreamBooks.App/App.xaml.cs`/`MainWindow.xaml.cs` (startup recovery), `Strings.resx` + `Strings.Designer.cs`
- Test: `tests/DreamBooks.App.Tests/RecoveryServiceTests.cs`, `tests/DreamBooks.App.Tests/DocumentLockTests.cs`

**Interfaces:**
- Consumes: `RecoveryStore`, `JournalRecorder`, `JournalReplayer`, `DocumentSession`, `DocumentFingerprint`.
- Produces:
  - `RecoveryService(RecoveryStore store, IFileDialogService dialogs)` exposes `IJournalSession Attach(DocumentSession session)` (calls `store.Begin(path, fingerprint)` lazily at the **first command**, creates a `JournalRecorder` over it), `Task<RestoreResult> TryRestoreAsync(DocumentSession session, CancellationToken ct)` (finds the journal for `session.Path`; compares header fingerprint with the session's open fingerprint: equal → `JournalReplayer.ReplayAsync`, then attaches a recorder; different → `dialogs.AskJournalMismatch()` returning `JournalMismatchAnswer { ApplyAnyway, Discard, Cancel }`; header path missing file → handled by the caller), `void OnSaved(DocumentSession session, JournalHeader newHeader)` (truncate), `void OnDiscarded(DocumentSession session)` (delete), `void OnSaveAs(string oldPath, DocumentSession session)` (delete the old path's journal, re-key to the new path with a fresh header), `RecoveredJournal? MostRecent()`; `record RestoreResult(bool Restored, int Applied, int Skipped, ViewEntry? LastView)`.
  - `DocumentLock`: `static IDisposable? TryAcquire(string documentPath)` using a named `Mutex` (`Local\DreamBooks-<DocumentKey>`); returns null when another process holds it. `MainViewModel` holds it while the document is open and shows `Strings.ErrorDocumentInUse` (and refuses to open) otherwise.
  - `MainViewModel`: after opening a document, `TryRestoreAsync`; on restore: marks dirty (it is, by baseline), shows `Strings.NoticeRestored` (one line), moves to `LastView.PageIndex` and publishes `RestoredView(zoom, scrollX, scrollY)` that `PageView` applies; startup: if `MostRecent()` exists and its file exists, open it automatically; if the file is missing show `Strings.ErrorJournalFileMissing` and offer `Discard`; `DiscardRequested` (Task 12) calls `OnDiscarded`; successful Save calls `OnSaved`; Save as calls `OnSaveAs`; `PageView` reports view state (page, zoom, scroll) debounced to 500 ms via an event the VM forwards to `JournalRecorder.RecordView`. `IFileDialogService` gains `JournalMismatchAnswer AskJournalMismatch()`.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public async Task First_command_creates_the_journal_and_each_command_appends_a_line()
[Fact] public async Task No_journal_exists_for_a_document_opened_and_closed_without_changes()
[Fact] public async Task Successful_save_truncates_the_journal_to_the_new_header()
[Fact] public async Task DontSave_deletes_the_journal()
[Fact] public async Task Abnormal_end_leaves_the_journal_and_the_next_open_restores_strokes_erasures_and_view()   // simulate by not disposing and starting a new service over the same store directory
[Fact] public async Task Restore_marks_the_document_dirty_and_shows_the_notice()
[Fact] public async Task Restore_does_not_create_duplicates_of_baseline_strokes()
[Fact] public async Task Mismatched_fingerprint_asks_and_Discard_deletes_ApplyAnyway_applies_and_Cancel_keeps()
[Fact] public async Task Erase_of_a_baseline_stroke_is_restored_after_a_crash()
[Fact] public async Task Journal_with_a_torn_last_line_restores_everything_before_it()
[Fact] public async Task Missing_document_keeps_the_journal_and_offers_discard()
[Fact] public async Task Save_as_deletes_the_old_journal_and_rekeys_to_the_new_path()
[Fact] public async Task Failed_save_leaves_the_journal_intact()
[Fact] public void DocumentLock_blocks_a_second_acquire_for_the_same_document_and_not_for_another()
[Fact] public async Task Opening_a_document_locked_by_another_process_is_refused_with_the_italian_message()
[Fact] public async Task Last_view_state_is_applied_after_restore()
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS; `dotnet format`. **Step 5:** Commit `feat: crash recovery from the journal, single writer per document and view state restore (FR-84, FR-86, FR-04)`.

---

### Task 14: Documents, publish and Fausto's checklist (controller)

- [ ] **Step 1:** Update `CLAUDE.md` project structure (Pdf references Core; `RecoveryStore` and the saver live in Pdf; PDFsharp not used) and keep the commands table accurate.
- [ ] **Step 2:** `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` clean; `dotnet publish src/DreamBooks.App -c Release -r win-x64 --self-contained`; verify `DreamBooks.App.exe` and `pdfium.dll`; delete any arm64 output.
- [ ] **Step 3:** Run the large synthetic-PDF measurement (open time, first page, save time, file growth over 10 saves) and report the numbers.
- [ ] **Step 4:** Give Fausto the manual checklist (in Italian) and wait: save then open the result in other readers; edit, save, reopen and erase old strokes; kill the app with Task Manager mid-session and restart (position and strokes back); answer "Non salvare" and restart (nothing restored); repeated saves keep the file size stable; his four real PDFs; Save as; a file changed on disk in between; a PDF with a signature if he has one; battery-less hard power-off is not required.
