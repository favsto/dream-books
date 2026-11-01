namespace DreamBooks.Core.Tests.Search;

/// <summary>
/// Runs the search scanner tests alone: one of them watches the global
/// <see cref="TaskScheduler.UnobservedTaskException"/> event, which other tests running in parallel could trip.
/// </summary>
[CollectionDefinition(nameof(SearchScannerCollection), DisableParallelization = true)]
public sealed class SearchScannerCollection;
