using DreamBooks.App.Services;
using DreamBooks.Pdf;

namespace DreamBooks.App.Tests;

/// <summary>A viewer without a real renderer: records release and reopen calls in a shared log.</summary>
internal sealed class FakeViewerHandle(List<string> log) : IViewerHandle
{
    public event EventHandler? CurrentChanged
    {
        add { }
        remove { }
    }

    public PdfDocumentInfo Info { get; set; } = new(IsEncrypted: false, HasRestrictions: false, SignatureCount: 0);

    public PdfRenderer? Current => null;

    public bool IsReleased { get; private set; }

    public string? OpenPath { get; private set; }

    public Exception? ReopenFailure { get; set; }

    public Task ReleaseAsync()
    {
        log.Add("release");
        IsReleased = true;
        return Task.CompletedTask;
    }

    public Task ReopenAsync(string path, CancellationToken ct)
    {
        log.Add("reopen:" + path);
        if (ReopenFailure is { } failure)
        {
            return Task.FromException(failure);
        }

        IsReleased = false;
        OpenPath = path;
        return Task.CompletedTask;
    }
}
