namespace DreamBooks.App.Services;

/// <summary>
/// A question for <see cref="ChoiceDialog"/>: the text, the button labels, the button Enter presses and the
/// answer Esc or the close box gives.
/// </summary>
internal sealed record ChoiceQuestion(string Message, IReadOnlyList<string> Labels, int DefaultIndex, int CancelIndex);
