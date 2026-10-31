namespace DreamBooks.Pdf;

/// <summary>
/// Puts a verified temporary copy in the place of the target file (FR-80, NFR-10). Plain
/// synchronous file operations: call them off the UI thread. The target is never deleted first:
/// an existing target is swapped atomically with <see cref="File.Replace(string, string, string?, bool)"/>
/// (no backup file), a missing one (Save as to a new path) receives the copy with an atomic move.
/// Sharing violations from antivirus or indexer locks are retried with a growing delay.
/// </summary>
public static class FileReplacer
{
    private const int _maxDelayMilliseconds = 2_000;

    private static readonly TimeSpan _defaultDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Replaces <paramref name="targetPath"/> with <paramref name="tempPath"/>, trying up to
    /// <paramref name="attempts"/> times, waiting <paramref name="delay"/> (default 50 ms) before the
    /// second attempt and twice as long before each later one. On final failure throws
    /// <see cref="ReplaceFailedException"/>: the target is unchanged or missing (see its
    /// <see cref="ReplaceFailedException.TargetExists"/>) and the temp file is kept, never deleted.
    /// </summary>
    /// <exception cref="FileNotFoundException">The temp file does not exist (nothing is touched).</exception>
    /// <exception cref="ReplaceFailedException">Every attempt failed.</exception>
    public static void Replace(string tempPath, string targetPath, int attempts = 5, TimeSpan? delay = null) =>
        ReplaceCore(tempPath, targetPath, attempts, delay ?? _defaultDelay, beforeAttempt: null);

    /// <summary>
    /// Test seam: <paramref name="beforeAttempt"/> runs before each attempt with its 1-based number.
    /// Returns the number of the attempt that succeeded.
    /// </summary>
    internal static int ReplaceCore(
        string tempPath, string targetPath, int attempts, TimeSpan delay, Action<int>? beforeAttempt)
    {
        ArgumentException.ThrowIfNullOrEmpty(tempPath);
        ArgumentException.ThrowIfNullOrEmpty(targetPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        if (!File.Exists(tempPath))
        {
            throw new FileNotFoundException("The temporary copy does not exist.");
        }

        TimeSpan wait = delay;
        for (int attempt = 1; ; attempt++)
        {
            beforeAttempt?.Invoke(attempt);
            try
            {
                // Checked on every attempt: if a failed swap ever left the target missing, the
                // next attempt moves the complete copy into place instead.
                if (File.Exists(targetPath))
                {
                    File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, targetPath, overwrite: false);
                }

                return attempt;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= attempts || !File.Exists(tempPath))
                {
                    throw new ReplaceFailedException(tempPath, targetPath, ex);
                }
            }

            if (wait > TimeSpan.Zero)
            {
                Thread.Sleep(wait);
            }

            wait = TimeSpan.FromMilliseconds(Math.Min(wait.TotalMilliseconds * 2, _maxDelayMilliseconds));
        }
    }
}
