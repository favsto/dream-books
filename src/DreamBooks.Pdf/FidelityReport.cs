namespace DreamBooks.Pdf;

/// <summary>
/// Result of <see cref="PdfFidelity.Compare"/>. <see cref="Differences"/> lists only what differs,
/// as categories and counts (never document content, NFR-12); <see cref="Summary"/> holds
/// informational counts for diagnostics.
/// </summary>
internal sealed record FidelityReport(IReadOnlyList<string> Differences)
{
    public bool IsFaithful => Differences.Count == 0;

    public IReadOnlyList<string> Summary { get; init; } = [];
}
