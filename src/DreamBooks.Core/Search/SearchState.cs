namespace DreamBooks.Core.Search;

/// <summary>Where a <see cref="SearchScanner"/>'s current run is (FR-13, FR-105).</summary>
public enum SearchState
{
    /// <summary><see cref="SearchScanner.Start(string, int)"/> has not been called.</summary>
    NotStarted,

    /// <summary>Pages are still to search; also while the scan is parked (paused or busy).</summary>
    Searching,

    /// <summary>Every page was searched (failed pages included): the index holds every hit.</summary>
    Completed,

    /// <summary>The hit cap stopped the scan: the index holds the first hits found and is marked truncated.</summary>
    Truncated,

    /// <summary>The scanner was disposed before the run ended.</summary>
    Cancelled,
}
