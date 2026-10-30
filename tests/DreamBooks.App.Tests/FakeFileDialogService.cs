using DreamBooks.App.Services;

namespace DreamBooks.App.Tests;

internal sealed class FakeFileDialogService : IFileDialogService
{
    public string? NextPath { get; set; }

    public bool ConfirmResult { get; set; } = true;

    public List<string> ConfirmMessages { get; } = [];

    public string? PickPdf() => NextPath;

    public bool Confirm(string message)
    {
        ConfirmMessages.Add(message);
        return ConfirmResult;
    }
}
