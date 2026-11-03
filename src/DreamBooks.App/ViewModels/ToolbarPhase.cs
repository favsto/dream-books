namespace DreamBooks.App.ViewModels;

/// <summary>Where the toolbar is in its automatic visibility (section 4).</summary>
public enum ToolbarPhase
{
    Visible,

    /// <summary>Fading out (200 ms, section 3); instant with Windows animations off.</summary>
    Retracting,

    Hidden,
}
