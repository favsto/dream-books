namespace DreamBooks.Core.Rendering;

/// <summary>Quality of a cached page bitmap (NFR-03).</summary>
public enum RenderQuality
{
    /// <summary>Small, quick render shown while the sharp one is on its way.</summary>
    Preview,

    /// <summary>Full-resolution render for the current zoom and DPI.</summary>
    Sharp,
}
