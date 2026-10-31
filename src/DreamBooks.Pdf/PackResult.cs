namespace DreamBooks.Pdf;

/// <summary>
/// Outcome of <see cref="PdfObjectStreamPacker.Pack"/>: input and output sizes in bytes, the
/// number of objects moved into object streams and the number of object streams written.
/// </summary>
internal sealed record PackResult(long InputBytes, long OutputBytes, int PackedObjects, int ObjectStreams);
