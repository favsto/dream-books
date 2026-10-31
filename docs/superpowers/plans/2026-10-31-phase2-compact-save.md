# Phase 2 follow-up: keep saved files compact (object stream packing) and verify the "file changed" prompt

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** After a save, the PDF is about as small as the original (today the first full rewrite by PDFium makes a book with compressed structure grow by +53% to +100%), without ever risking an unreadable file; and prove with a real test that saving after another program changed the file asks the user first.

**Architecture:** PDFium's full rewrite stays the source of truth. A new post-processing step in `DreamBooks.Pdf` re-packs the plain (uncompressed) objects of PDFium's regular output into Flate-compressed object streams plus a cross-reference stream, producing a second temp file. The saver verifies the packed file (the existing verification plus a fidelity comparison against the unpacked one) and, on ANY problem, silently falls back to the unpacked, already verified copy: the failure mode is "bigger file", never "broken file".

**Tech Stack:** unchanged (.NET 10, PDFium via bblanchon.PDFium.Win32, `System.IO.Compression.ZLibStream`, xUnit). No new NuGet packages.

**Context from Fausto's test (2026-10-31):** his 1,076-page e-book (4,252,788 bytes) becomes 6,517,124 bytes after the rewrite; gzip of both files is ~4.1 MB, so the growth is only uncompressed structure: PDFium unpacks the original's 87 object streams into ~18,000 plain objects. His 1,500-page file doubled. After the first save the size stays stable (+~10 KB per added stroke).

## Global Constraints

- All the phase 2 constraints still apply (Core free of WPF/PDFium; PDFium work on the PDFium thread; the user's original file is never modified before verification; temp files never left behind; no document content/paths in exception messages shown to the user; nullable on; file-scoped namespaces; one public type per file; warnings are errors; TDD in Pdf; never run the WPF app; do not stage `.claude/settings.json`; commit trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`; `dotnet build` 0 warnings, `dotnet test` green, `dotnet format --verify-no-changes` clean at the end of every task).
- Packing is an OPTIMISATION: any failure (unsupported shape, exception, verification mismatch, no real size gain) falls back to the unpacked verified copy; only cancellation propagates. The packed output must be valid per the PDF 1.5+ specification (ISO 32000-1 sections 7.5.7 object streams and 7.5.8 cross-reference streams), not merely readable by PDFium: Acrobat is stricter.
- Fausto's real PDFs live in `C:\Users\Favsto\workspace\temp` (or `DREAMBOOKS_FIXTURES`): tests only read COPIES, never print names or text, skip cleanly when absent. Do not commit them or their names.

## Review Focus

1. **Spec-valid output**: objects inside an ObjStm must be generation 0, non-stream, not the Encrypt dictionary, and each ObjStm needs `/Type /ObjStm /N /First` (+ Flate); the XRef stream needs `/Type /XRef /Size /W /Index /Root` (+ `/Info`, `/ID` when present), its own entry, correct `startxref`, header version ≥ 1.5, no leftover classic `xref`/`trailer`. Pinned in Task 1 by structural tests and a reader-independent byte-level checker.
2. **Fallback on every failure** returns the unpacked verified copy and leaves no extra temp file. Pinned in Task 2.
3. **Stability**: repeated saves of an already packed file stay stable (the next rewrite unpacks again, then re-packs). Pinned in Task 2 with the real e-book copy.
4. **Large inputs**: memory/time bounded (skip packing above a size limit, never hold more than needed).
5. **The "file changed" prompt**: Save after an external change asks (Task 3).

---

### Task 1: `PdfObjectStreamPacker`

**Files:**
- Create: `src/DreamBooks.Pdf/PdfObjectStreamPacker.cs`, `src/DreamBooks.Pdf/PdfPackException.cs` (internal), `src/DreamBooks.Pdf/PackResult.cs`
- Test: `tests/DreamBooks.Pdf.Tests/PdfObjectStreamPackerTests.cs`, `tests/DreamBooks.Pdf.Tests/PdfStructureChecker.cs` (test helper: independent minimal parser of the packed output that validates the structural rules above without PDFium)

**Interfaces:**
- Produces:
  - `internal sealed record PackResult(long InputBytes, long OutputBytes, int PackedObjects, int ObjectStreams)`.
  - `internal sealed class PdfPackException(string message) : Exception` — "this input is not a shape I handle" (message has no content/paths).
  - `internal static class PdfObjectStreamPacker { public static PackResult Pack(string inputPath, string outputPath, CancellationToken ct = default); }` — reads PDFium's regular full-rewrite output (header, `N 0 obj … endobj` bodies, ONE classic `xref` table, `trailer`, `startxref`, `%%EOF`) and writes a new file with the eligible plain objects packed into Flate ObjStms (up to 100 objects each) and a cross-reference stream. Never modifies the input; output is created with `FileMode.CreateNew` and deleted on any failure. Throws `PdfPackException` for any input it does not fully understand (e.g. already has an xref stream, `/Prev`, `/Encrypt` in the trailer, hybrid `/XRefStm`, more than one xref section, object offsets that do not match, a size above 512 MB); `OperationCanceledException` on cancellation; never returns a file it cannot vouch for.

Rules (decided here): stream objects are copied verbatim (bytes unchanged, including their `/Length`); eligible = generation 0, not a stream object, not the Encrypt dictionary, and (to keep readers that need it happy) the objects referenced by the trailer `/Root` and `/Info` ARE packable per spec, but the xref-stream's `/Root`/`/Info`/`/ID` go in its dictionary; dictionary boundaries are found with a small tokenizer that respects literal strings (balanced parentheses, escapes), hex strings and comments, and a stream is detected as `>>` followed by the keyword `stream`; object numbers keep their numbers (no renumbering) and are listed in ascending order inside each ObjStm; the xref stream gets the next free object number (`/Size` = max+2 when needed), `/W [1 4 2]` widened automatically when offsets or indexes need more bytes, `/Index` covers every entry, FlateDecode without predictor; header becomes `%PDF-1.5` (or the existing version if higher); keep the original binary-marker comment line if present. Pack only if it saves at least 5% of the input, otherwise throw `PdfPackException` with message "no gain" (the caller falls back).

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void Packs_a_pdfium_rewrite_of_a_synthetic_document_and_the_result_is_faithful()      // TestPdfFactory.WriteWithFeatures (outline, links, annotations, metadata) → PdfFullRewrite → Pack → PdfFidelity.Compare(rewritten, packed).IsFaithful
[Fact] public void Packed_output_has_ObjStm_and_a_cross_reference_stream_and_no_classic_xref()       // via PdfStructureChecker
[Fact] public void Header_version_is_at_least_1_5()
[Fact] public void Stream_objects_are_copied_byte_for_byte()                                        // compare each stream payload before/after through PdfStructureChecker
[Fact] public void Objects_inside_object_streams_are_generation_0_and_not_streams()                 // structural rule
[Fact] public void Xref_stream_entries_cover_every_object_and_offsets_point_to_object_headers()      // independent checker walks every offset
[Fact] public void Trailer_root_info_and_id_survive_in_the_xref_stream_dictionary()
[Fact] public void Dictionary_tokenizer_handles_parentheses_hex_strings_and_comments_in_strings()    // internal helper test with tricky bodies like <</T (a ) b \( c) /H <48656C6C6F> % x>> 
[Fact] public void Input_that_already_has_an_xref_stream_is_rejected()
[Fact] public void Input_with_Encrypt_in_the_trailer_is_rejected()                                    // TestPdfFactory.WriteEncryptedRestricted (not rewritten by PDFium: build the trailer shape by hand if needed)
[Fact] public void Garbage_and_truncated_input_is_rejected_without_creating_an_output_file()
[Fact] public void Input_with_no_gain_is_rejected_with_the_no_gain_message()                          // tiny file: output not smaller
[Fact] public void Input_file_is_never_modified()                                                    // SHA-256 before == after
[Fact] public void Cancellation_leaves_no_output_file()
[Fact] public void Packed_real_ebook_copy_is_at_least_20_percent_smaller_and_faithful_and_PDFium_renders_it()   // reads a COPY of the largest real pdf (skip if absent): Rewrite → Pack; assert OutputBytes < 0.8 × rewritten size, PdfFidelity.Compare(rewritten, packed).IsFaithful (pages, text, outline, pixels), print sizes only
[Fact] public void Packing_the_real_ebook_copy_stays_within_memory_and_time_limits()                  // < 20 s on the test machine, no giant allocations (assert GC.GetTotalAllocatedBytes delta < 8 × file size)
```

- [ ] **Step 2:** `dotnet test tests/DreamBooks.Pdf.Tests` → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS; `dotnet format`.
- [ ] **Step 5:** Commit `feat: pack plain objects into compressed object streams after a PDFium rewrite (NFR-06, FR-80)`.

---

### Task 2: Use the packer in the saver, with fallback

**Files:**
- Modify: `src/DreamBooks.Pdf/PdfDocumentSaver.cs` (and only what it needs)
- Test: `tests/DreamBooks.Pdf.Tests/PdfDocumentSaverTests.cs` (extend), `tests/DreamBooks.Pdf.Tests/PackedSaveTests.cs` (new)

**Interfaces:**
- Consumes: `PdfObjectStreamPacker.Pack`, `PdfFidelity.Compare`, the existing verification.
- Produces: `PdfDocumentSaver` keeps `WriteVerifiedCopyAsync(sourcePath, destinationPath, changes, ct)`. New optional constructor flag `bool packObjects = true`. Flow after the existing write + verification of the unpacked temp (temp1): try `Pack(temp1 → temp2)` (temp2 sibling of the destination with the same naming pattern); verify temp2 with the SAME stroke/annotation verification used for temp1 plus `PdfFidelity.Compare(temp1, temp2).IsFaithful`; if all is well delete temp1 and return temp2; on ANY non-cancellation problem delete temp2 (if created) and return temp1 unchanged; cancellation deletes both temps. Packing is skipped when the verified copy is above 300 MB.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public async Task Save_returns_a_packed_copy_that_reopens_with_the_same_strokes()
[Fact] public async Task Packed_save_of_the_real_ebook_copy_is_close_to_the_original_size()         // real copy (skip if absent): original size S; after Save with 3 strokes: result < 1.15 × S
[Fact] public async Task Repeated_saves_of_the_packed_real_ebook_copy_stay_stable()                  // 3 saves: sizes within 1% + added strokes' size of the previous one
[Fact] public async Task Pack_failure_falls_back_to_the_unpacked_verified_copy()                     // internal hook makes Pack throw PdfPackException → returned file is the unpacked copy, valid, strokes present, no stray temp
[Fact] public async Task Pack_verification_mismatch_falls_back_and_removes_the_packed_temp()          // hook corrupts the packed copy
[Fact] public async Task Unexpected_exception_in_Pack_falls_back()                                    // e.g. IOException
[Fact] public async Task Cancellation_during_packing_removes_both_temp_files()
[Fact] public async Task Source_and_destination_are_untouched_in_every_branch()                       // SHA-256
[Fact] public async Task Packing_can_be_disabled()
[Fact] public async Task A_packed_source_file_can_be_saved_again_and_edited()                         // source already packed (output of a previous save), erase+add strokes
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS; full suite green; `dotnet format`.
- [ ] **Step 5:** Commit `feat: saver returns a compact verified copy and falls back to the plain one on any problem (NFR-06, NFR-10)`.

---

### Task 3: Verify the "file changed" prompt end to end

**Files:**
- Test: `tests/DreamBooks.App.Tests/FileChangedPromptTests.cs` (new); modify product code ONLY if the test exposes a bug.

**Interfaces:** consumes `MainViewModel`, `DocumentSaveService`, `FakeFileDialogService`, `TestPdfFactory`, real `PdfRenderer`/`PdfDocumentSaver`.

Context: Fausto modified the open file with another program (possible: PDFium lets others write in place), pressed Ctrl+S and saw no "file changed" question; the question only appeared at the next start (journal mismatch dialog). Reading the code the check looks right (`IsUnchangedAsync` compares `DocumentFingerprint` with `session.OpenFingerprint`), so find out why it did not fire in his case. Candidate causes to test: (a) `OpenFingerprint` refreshed by something between open and save (restore flow, `MarkSaved`, page changes); (b) the external change keeps length and last-write time and the first/last 64 KB identical (e.g. a change in the middle of a large file: the fingerprint hashes only the edges; length and last-write time normally differ — verify last-write handling), (c) Save with a clean session short-circuits somewhere, (d) keyboard focus: Ctrl+S not reaching the window while the pen/ink surface has capture.

- [ ] **Step 1: Failing tests** (use real files in a temp folder; fakes for dialogs)

```csharp
[Fact] public async Task Save_asks_when_the_file_was_changed_by_another_program_after_it_was_opened()        // open real generated PDF via VM, draw a stroke, append bytes / rewrite the file externally (also: change a byte in the middle keeping length), Save → AskFileChanged called once
[Fact] public async Task Save_asks_even_when_the_document_has_no_unsaved_changes()                          // not dirty: external change, Save → still asks (or document the deliberate behaviour and why)
[Fact] public async Task Save_asks_after_a_restore_from_the_journal_if_the_file_changed_since()             // restore flow then external change
[Fact] public async Task Save_does_not_ask_when_nothing_changed()
[Fact] public async Task A_change_that_keeps_length_and_last_write_time_but_alters_the_middle_is_a_known_limit()  // documents the fingerprint's blind spot: if the test shows the prompt does NOT fire, REPORT it (do not hide): propose a cheap mitigation (include a sampled hash of the middle, or compare a full SHA-256 only when length ≤ 64 MB) and implement it test-first if small
```

- [ ] **Step 2:** Run; if a test fails it exposes the bug: fix test-first. If all pass, the prompt works for the scenarios tried: say so plainly and list which scenario could still differ from Fausto's (ask for his exact steps). **Step 3:** build/test/format clean. **Step 4:** Commit `test: pin the file-changed prompt on real files (FR-80)` (plus any fix).

---

### Task 4: Controller

- [ ] Re-measure with Fausto's real e-book copy: original size, size after one save with strokes, after three saves; run the fidelity gate on all four real PDFs through the saver (not only the packer); `dotnet publish -c Release -r win-x64 --self-contained`; give Fausto the checklist (Acrobat/Edge/Chrome opening of a PACKED saved file is the key check) and wait.
