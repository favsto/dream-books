using DreamBooks.Core.Documents;

namespace DreamBooks.App.Tests;

/// <summary>
/// Writes "source bytes + marker" to a temp file next to the destination, like the real writer's naming;
/// can fail or pause (<see cref="Gate"/>) to test the pipeline around it.
/// </summary>
internal sealed class FakeDocumentWriter(List<string>? log = null) : IDocumentWriter
{
    public static readonly byte[] Marker = "saved-by-fake"u8.ToArray();

    public List<(string Source, string Destination, IReadOnlyList<PageChange> Changes)> Calls { get; } = [];

    public Exception? Failure { get; set; }

    /// <summary>When set, the write waits for it before producing the temp file.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string? LastTempPath { get; private set; }

    private int _written;

    public async Task<string> WriteVerifiedCopyAsync(
        string sourcePath, string destinationPath, IReadOnlyList<PageChange> changes, CancellationToken ct = default)
    {
        Calls.Add((sourcePath, destinationPath, changes));
        log?.Add("write");
        Started.TrySetResult();
        if (Gate is { } gate)
        {
            await gate.Task;
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        var full = Path.GetFullPath(destinationPath);
        var temp = Path.Combine(Path.GetDirectoryName(full)!, $"{Path.GetFileName(full)}.{++_written:x8}.drbk-tmp");
        await File.WriteAllBytesAsync(temp, [.. await File.ReadAllBytesAsync(sourcePath, ct), .. Marker], ct);
        LastTempPath = temp;
        return temp;
    }
}
