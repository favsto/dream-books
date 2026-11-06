using DreamBooks.Core.Model;

namespace DreamBooks.App.ViewModels;

/// <summary>One preset pen colour (FR-25) and its Italian name for screen readers (NFR-31).</summary>
public sealed record ColorSwatch(InkColor Color, string Name);
