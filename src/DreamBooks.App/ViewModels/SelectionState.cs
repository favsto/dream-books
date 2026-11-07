namespace DreamBooks.App.ViewModels;

/// <summary>Where the lasso selection is in its life (FR-50, FR-51).</summary>
public enum SelectionState
{
    /// <summary>Nothing selected.</summary>
    None,

    /// <summary>Strokes selected and at rest.</summary>
    Selected,

    /// <summary>The selection is being dragged; the page is untouched until commit.</summary>
    Moving,

    /// <summary>The selection is being resized; the page is untouched until commit.</summary>
    Resizing,
}
