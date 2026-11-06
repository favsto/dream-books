namespace DreamBooks.App.Tests;

/// <summary>
/// Runs the marks and multi-page integration tests alone: they time scans, measure the process's private memory and watch the
/// global <see cref="TaskScheduler.UnobservedTaskException"/> event, which tests running in parallel would disturb.
/// </summary>
[CollectionDefinition(nameof(MarkIntegrationCollection), DisableParallelization = true)]
public sealed class MarkIntegrationCollection;
