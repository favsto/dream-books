namespace DreamBooks.Core.Model;

/// <summary>Outcome of parsing a stroke payload. <see cref="Stroke"/> is non-null only when Status is Ok.</summary>
public readonly record struct StrokeParseResult(StrokeParseStatus Status, InkStroke? Stroke);
