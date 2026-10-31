namespace DreamBooks.Core.Tests.Marks;

/// <summary>
/// Runs the mark scanner tests alone: one of them watches the global
/// <see cref="TaskScheduler.UnobservedTaskException"/> event, which other tests running in parallel could trip.
/// </summary>
[CollectionDefinition(nameof(MarkScannerCollection), DisableParallelization = true)]
public sealed class MarkScannerCollection;
