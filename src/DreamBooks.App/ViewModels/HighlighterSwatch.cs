using DreamBooks.Core.Model;

namespace DreamBooks.App.ViewModels;

/// <summary>One highlighter colour preset (FR-41) and its Italian name for screen readers (NFR-31).</summary>
public sealed record HighlighterSwatch(InkColor Color, string Name);
