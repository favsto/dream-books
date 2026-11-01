using DreamBooks.Core.Marks;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// One row of the marks list (FR-103): the mark, its page caption ("pag. 12") and its automation name, unique in the
/// list ("Segno a pagina 12, margine sinistro, riga 4": two marks on one side are always lines apart).
/// </summary>
public sealed record MarkItem(Mark Mark, string PageLabel, string AutomationName);
