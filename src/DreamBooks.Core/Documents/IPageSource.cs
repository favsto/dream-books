namespace DreamBooks.Core.Documents;

/// <summary>Read access to the pages of an opened document.</summary>
/// <remarks>
/// <see cref="GetPageDimensions"/> and the synchronous part of <see cref="LoadStrokesAsync"/> run while
/// <see cref="DocumentSession"/> holds its internal lock: they must be cheap and must never call back
/// into the session.
/// </remarks>
public interface IPageSource
{
    int PageCount { get; }

    PageDimensions GetPageDimensions(int pageIndex);

    Task<PageStrokes> LoadStrokesAsync(int pageIndex, CancellationToken ct = default);
}
