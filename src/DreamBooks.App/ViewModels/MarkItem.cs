using DreamBooks.Core.Marks;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// One row of the marks list (FR-103): the mark, its page caption ("pag. 12") and its automation name ("Segno a pagina
/// 12, margine sinistro", or "..., al centro" for a highlight in the middle of the page; no line number until real line
/// heights arrive in 3b-2).
/// </summary>
public sealed record MarkItem(Mark Mark, string PageLabel, string AutomationName);
