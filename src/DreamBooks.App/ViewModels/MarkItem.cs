using DreamBooks.Core.Marks;

namespace DreamBooks.App.ViewModels;

/// <summary>One row of the marks list (FR-103): the mark and its page caption ("pag. 12").</summary>
public sealed record MarkItem(Mark Mark, string PageLabel);
