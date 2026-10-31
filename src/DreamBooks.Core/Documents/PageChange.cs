using DreamBooks.Core.Model;

namespace DreamBooks.Core.Documents;

/// <summary>The net stroke change on one page: strokes removed by id and strokes added.</summary>
public sealed record PageChange(int PageIndex, IReadOnlyList<Guid> RemovedIds, IReadOnlyList<InkStroke> Added);
