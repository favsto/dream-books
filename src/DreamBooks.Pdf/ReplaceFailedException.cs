namespace DreamBooks.Pdf;

/// <summary>
/// The verified copy could not take the target's place after every retry (NFR-10). The target is
/// unchanged OR MISSING (a failed Windows <c>ReplaceFile</c> without a backup file can leave the
/// target removed); the complete verified copy is at <see cref="TempPath"/>. Check
/// <see cref="TempExists"/> (or <c>File.Exists(TempPath)</c>) before telling the user where it is.
/// The message has no paths; <see cref="Exception.InnerException"/> is a system exception whose
/// message may contain paths, so it must never be displayed or logged.
/// </summary>
public sealed class ReplaceFailedException : Exception
{
    public ReplaceFailedException(string tempPath, string targetPath, Exception innerException)
        : base("The saved copy could not replace the file.", innerException)
    {
        TempPath = tempPath;
        TargetExists = File.Exists(targetPath);
        TempExists = File.Exists(tempPath);
    }

    /// <summary>Where the complete, verified copy is (if <see cref="TempExists"/>).</summary>
    public string TempPath { get; }

    /// <summary>Whether the target file existed when this exception was created.</summary>
    public bool TargetExists { get; }

    /// <summary>Whether the verified copy existed when this exception was created.</summary>
    public bool TempExists { get; }
}
