namespace DreamBooks.App.Services;

/// <summary>
/// The outcome of a save, the Italian message to show (null for none; never exception text, NFR-12) and,
/// when the file was saved, what was saved.
/// </summary>
public sealed record SaveResult(SaveOutcome Outcome, string? Message = null, DocumentSavedEventArgs? Saved = null);
