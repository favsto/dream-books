using System.Globalization;
using DreamBooks.Core.Search;

namespace DreamBooks.Core.Tests.Search;

/// <summary>Case- and accent-insensitive folding with a map back to the source characters (FR-13).</summary>
public class TextFoldingTests
{
    [Fact]
    public void Folding_lowercases_and_removes_accents()
    {
        Assert.Equal("perche e cosi citta", TextFolding.Fold("Perché È così CITTÀ").Folded);
        Assert.Equal("naive cafe uber", TextFolding.Fold("naïve CAFÉ Über").Folded);
    }

    [Fact]
    public void Precomposed_and_decomposed_accents_fold_the_same()
    {
        FoldedText decomposed = TextFolding.Fold("é"); // e + combining acute

        Assert.Equal("e", decomposed.Folded);
        Assert.Equal([0], decomposed.SourceIndex);
        Assert.Equal(TextFolding.Fold("é").Folded, decomposed.Folded);
    }

    [Fact]
    public void Ligatures_are_expanded()
    {
        FoldedText folded = TextFolding.Fold("Uﬃcio ﬁne ﬂuido");

        Assert.Equal("ufficio fine fluido", folded.Folded);

        // Every letter of a ligature maps to the ligature itself.
        Assert.Equal([0, 1, 1, 1, 2, 3, 4], folded.SourceIndex[..7]);
    }

    [Fact]
    public void Whitespace_runs_and_line_breaks_collapse_to_one_space()
    {
        FoldedText folded = TextFolding.Fold("uno  \t due\r\ntre  quattro");

        Assert.Equal("uno due tre quattro", folded.Folded);

        // The collapsed space points at the first whitespace character of its run.
        Assert.Equal(3, folded.SourceIndex[3]);
        Assert.Equal(10, folded.SourceIndex[7]);
    }

    [Fact]
    public void Control_characters_count_as_whitespace()
    {
        Assert.Equal("a b", TextFolding.Fold("a\u0002\u0000b").Folded);
    }

    [Fact]
    public void SourceIndex_is_monotonic_and_maps_back_to_the_original_characters()
    {
        string source = "L'Ùltima  ﬁne\r\n di \U0001D400 cosÌ";

        FoldedText folded = TextFolding.Fold(source);

        Assert.Equal(folded.Folded.Length, folded.SourceIndex.Length);
        for (int i = 1; i < folded.SourceIndex.Length; i++)
        {
            Assert.True(folded.SourceIndex[i] >= folded.SourceIndex[i - 1], $"not monotonic at {i}");
        }

        for (int i = 0; i < folded.Folded.Length; i++)
        {
            int s = folded.SourceIndex[i];
            Assert.InRange(s, 0, source.Length - 1);
            Assert.False(char.IsLowSurrogate(source[s]), $"index {i} points into a surrogate pair");
            string original = char.IsHighSurrogate(source[s]) ? source.Substring(s, 2) : source[s].ToString();
            if (folded.Folded[i] == ' ')
            {
                Assert.True(string.IsNullOrWhiteSpace(original), $"space at {i} maps to a non-space");
            }
            else
            {
                // The folded character comes from folding that very source character.
                Assert.Contains(folded.Folded[i], TextFolding.Fold(original).Folded);
            }
        }
    }

    [Fact]
    public void Characters_outside_the_BMP_keep_their_surrogate_pair_and_map_to_its_first_unit()
    {
        FoldedText folded = TextFolding.Fold("a\U0001F600b");

        Assert.Equal("a\U0001F600b", folded.Folded);
        Assert.Equal([0, 1, 1, 3], folded.SourceIndex);
    }

    [Fact]
    public void Lone_surrogates_do_not_throw_and_stay_unmatched()
    {
        FoldedText folded = TextFolding.Fold("a\uD800b\uDC00");

        Assert.Equal(4, folded.Folded.Length);
        Assert.Equal([0, 1, 2, 3], folded.SourceIndex);
    }

    [Fact]
    public void Empty_and_whitespace_queries_fold_to_empty()
    {
        Assert.Equal(string.Empty, TextFolding.FoldQuery(string.Empty));
        Assert.Equal(string.Empty, TextFolding.FoldQuery("   \t\r\n "));
        Assert.Equal(string.Empty, TextFolding.FoldQuery("́")); // a lone combining mark
        Assert.Empty(TextFolding.Fold(string.Empty).Folded);
        Assert.Empty(TextFolding.Fold(string.Empty).SourceIndex);
    }

    [Fact]
    public void Queries_are_trimmed_and_their_inner_whitespace_collapses()
    {
        Assert.Equal("mondo senza fine", TextFolding.FoldQuery("  Mondo \t senza\r\nFINE  "));
    }

    [Fact]
    public void Folding_is_idempotent()
    {
        string once = TextFolding.FoldQuery("Città  dell'ﬁne È");

        Assert.Equal(once, TextFolding.FoldQuery(once));
    }

    [Fact]
    public void Folding_is_culture_invariant()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        CultureInfo savedUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");

            Assert.Equal("istanbul i", TextFolding.Fold("ISTANBUL I").Folded);
            Assert.Equal("i", TextFolding.FoldQuery("I"));
            Assert.Equal("i", TextFolding.FoldQuery("İ")); // capital I with dot above
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
            CultureInfo.CurrentUICulture = savedUi;
        }
    }

    [Fact]
    public void Fold_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => TextFolding.Fold(null!));
        Assert.Throws<ArgumentNullException>(() => TextFolding.FoldQuery(null!));
    }

    [Fact]
    public void Search_result_types_compare_by_value()
    {
        var a = new SearchHit(3, [new SearchRect(1, 2, 3, 4), new SearchRect(5, 6, 7, 8)]);
        var b = new SearchHit(3, new List<SearchRect> { new(1, 2, 3, 4), new(5, 6, 7, 8) });

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new SearchHit(3, [new SearchRect(1, 2, 3, 4)]));
        Assert.NotEqual(a, new SearchHit(4, a.Rects));

        var r1 = new SearchPageResult(true, [a], Truncated: false);
        var r2 = new SearchPageResult(true, [b], Truncated: false);
        Assert.Equal(r1, r2);
        Assert.NotEqual(r1, r2 with { Truncated = true });
    }
}
