namespace DreamBooks.Core.Rendering;

/// <summary>
/// One page the view wants a bitmap for. A lower <see cref="Priority"/> is more urgent; 0 means visible.
/// A <see cref="PreviewWidth"/> or <see cref="PreviewHeight"/> of 0 or less disables the preview pass for
/// that page; a non-positive sharp size means no sharp render is wanted.
/// </summary>
public readonly record struct RenderRequest(
    int PageIndex,
    int Priority,
    int SharpWidth,
    int SharpHeight,
    int PreviewWidth,
    int PreviewHeight);
