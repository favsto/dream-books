namespace DreamBooks.Core.Tests.Rendering;

/// <summary>
/// Runs the scheduler tests alone: one of them watches the global
/// <see cref="TaskScheduler.UnobservedTaskException"/> event, which other tests running in parallel could trip.
/// </summary>
[CollectionDefinition(nameof(RenderSchedulerCollection), DisableParallelization = true)]
public sealed class RenderSchedulerCollection;
