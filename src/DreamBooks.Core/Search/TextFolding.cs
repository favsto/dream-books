using System.Globalization;
using System.Text;

namespace DreamBooks.Core.Search;

/// <summary>
/// Case- and accent-insensitive folding for text search (FR-13): invariant lower case, compatibility
/// decomposition (NFKD) with combining marks dropped, so "È" becomes "e" and the ligature "ﬁ" becomes "fi"; runs
/// of whitespace and control characters (line breaks, characters PDF text extraction generates) collapse to one
/// space. Culture-invariant and deterministic. Lone surrogates are kept as they are (they never match typed text).
/// </summary>
public static class TextFolding
{
    /// <summary>Folds <paramref name="text"/> and maps every folded character back to its source character.</summary>
    public static FoldedText Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var folded = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        bool inSpace = false;

        void Emit(char c, int source)
        {
            folded.Append(c);
            map.Add(source);
        }

        void Space(int source)
        {
            if (!inSpace)
            {
                Emit(' ', source);
                inSpace = true;
            }
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            int width = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            if (width == 1 && IsSeparator(c))
            {
                Space(i);
            }
            else if (c < 0x80)
            {
                inSpace = false;
                Emit(char.ToLowerInvariant(c), i);
            }
            else if (width == 1 && char.IsSurrogate(c))
            {
                inSpace = false;
                Emit(c, i);
            }
            else
            {
                string decomposed = text.Substring(i, width).Normalize(NormalizationForm.FormKD);
                var kept = new StringBuilder(decomposed.Length);
                for (int k = 0; k < decomposed.Length; k++)
                {
                    UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(decomposed, k);
                    if (category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                        or UnicodeCategory.EnclosingMark))
                    {
                        kept.Append(decomposed[k]);
                    }
                }

                // Simple case mapping: the invariant lower case keeps the length, so surrogate pairs stay whole.
                foreach (char f in kept.ToString().ToLowerInvariant())
                {
                    if (IsSeparator(f))
                    {
                        Space(i);
                    }
                    else
                    {
                        inSpace = false;
                        Emit(f, i);
                    }
                }
            }

            i += width;
        }

        return new FoldedText(folded.ToString(), [.. map]);
    }

    /// <summary>Folds a typed query: as <see cref="Fold"/>, trimmed; empty when nothing searchable is left.</summary>
    public static string FoldQuery(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Fold(query).Folded.Trim(' ');
    }

    private static bool IsSeparator(char c) => char.IsWhiteSpace(c) || char.IsControl(c);
}
