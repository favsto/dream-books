# .NET and architecture conventions

## From the requirements (mandatory)

- Stroke coordinates are always stored in PDF page points (1/72 inch);
  conversion to screen coordinates happens only in the view layer (FR-26).
- Every model change goes through an `IUndoableCommand` executed by
  `UndoManager`. Nothing mutates the model directly (FR-60–FR-66).
- `DreamBooks.Core` never references WPF, PDFium or PDFsharp.
- PDF rendering and saving run off the UI thread; the UI thread only handles
  pen input and drawing (NFR-04).
- Saving writes to a temporary file and replaces the original only after a
  successful write (NFR-10). Autosave never touches the original file (FR-84).
- App-private data stored in the PDF carries a schema version number.
- Never log document content (NFR-12). The app makes no network calls (NFR-40).

## Proposed defaults (to confirm with Fausto)

- Nullable reference types enabled in every project.
- File-scoped namespaces; one public type per file, file named after the type.
- Async methods end in `Async` and accept a `CancellationToken` when they do I/O.
