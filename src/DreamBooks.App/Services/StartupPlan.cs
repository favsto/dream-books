namespace DreamBooks.App.Services;

/// <summary>What the app opens at start-up (FR-86, FR-03).</summary>
public enum StartupAction
{
    /// <summary>No document: the start screen shows.</summary>
    StartScreen,

    /// <summary>The file the app was started with ("Apri con").</summary>
    OpenFile,

    /// <summary>The last journalled session comes back.</summary>
    Restore,
}

/// <summary>The start-up order: a file argument wins, then the restored session, otherwise the start screen.</summary>
internal static class StartupPlan
{
    public static StartupAction Decide(string? commandLineFile, bool restorable)
    {
        if (!string.IsNullOrWhiteSpace(commandLineFile))
        {
            return StartupAction.OpenFile;
        }

        return restorable ? StartupAction.Restore : StartupAction.StartScreen;
    }

    /// <summary>The first argument that is not a flag (<c>-x</c>, <c>--x</c>, <c>/x</c>) and not blank; null when there is none.</summary>
    public static string? FileFromArguments(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith('-') && !a.StartsWith('/'));
    }
}
