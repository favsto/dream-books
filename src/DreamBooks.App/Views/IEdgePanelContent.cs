namespace DreamBooks.App.Views;

/// <summary>
/// Content of an <see cref="EdgePanel"/> tab that wants to know when it is shown (the panel opened on it, or its tab
/// chosen) and hidden (the panel closed, or the other tab chosen).
/// </summary>
internal interface IEdgePanelContent
{
    /// <summary>The content just became the one shown, after the panel's layout was requested. UI thread.</summary>
    void OnShown();

    /// <summary>The content is no longer shown. UI thread.</summary>
    void OnHidden();
}
