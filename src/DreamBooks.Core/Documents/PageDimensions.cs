namespace DreamBooks.Core.Documents;

/// <summary>Size of a page in PDF page points (1/72 inch).</summary>
public readonly record struct PageDimensions(double WidthPt, double HeightPt);
