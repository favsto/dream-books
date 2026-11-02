using DreamBooks.Core.Documents;
using DreamBooks.Core.Journal;
using static DreamBooks.Core.Tests.Journal.JournalTestData;

namespace DreamBooks.Core.Tests.Journal;

public class JournalCodecTests
{
    [Fact]
    public void Every_entry_type_round_trips_through_the_codec()
    {
        var header = new JournalHeader(JournalCodec.CurrentFormatVersion, "C:\\Libri\\perché \"io\".pdf", Fingerprint);
        var added = RichStroke();
        var second = Stroke(50);
        var piece = RichStroke();
        var removed = Guid.NewGuid();

        var backHeader = JournalCodec.ParseHeader(JournalCodec.SerializeHeader(header));
        var add = Assert.IsType<AddEntry>(JournalCodec.ParseEntry(JournalCodec.SerializeEntry(new AddEntry(3, [added, second]))));
        var erase = Assert.IsType<EraseEntry>(JournalCodec.ParseEntry(JournalCodec.SerializeEntry(
            new EraseEntry(7, [removed], [new IndexedStroke(4, piece)]))));
        var view = JournalCodec.ParseEntry(JournalCodec.SerializeEntry(new ViewEntry(12, 1.75, 30.5, 1234.25)));

        Assert.Equal(header, backHeader);
        Assert.Equal(3, add.PageIndex);
        Assert.Equal(2, add.Strokes.Count);
        AssertSameStroke(added, add.Strokes[0]);
        AssertSameStroke(second, add.Strokes[1]);
        Assert.Equal(7, erase.PageIndex);
        Assert.Equal([removed], erase.RemovedIds);
        var indexed = Assert.Single(erase.Added);
        Assert.Equal(4, indexed.Index);
        AssertSameStroke(piece, indexed.Stroke);
        Assert.Equal(new ViewEntry(12, 1.75, 30.5, 1234.25), view);
    }

    [Fact]
    public void Empty_add_and_erase_entries_round_trip()
    {
        var add = Assert.IsType<AddEntry>(JournalCodec.ParseEntry(JournalCodec.SerializeEntry(new AddEntry(0, []))));
        var erase = Assert.IsType<EraseEntry>(JournalCodec.ParseEntry(JournalCodec.SerializeEntry(new EraseEntry(1, [], []))));

        Assert.Empty(add.Strokes);
        Assert.Empty(erase.RemovedIds);
        Assert.Empty(erase.Added);
    }

    [Fact]
    public void Entry_lines_are_single_line_ascii()
    {
        var lines = new[]
        {
            JournalCodec.SerializeHeader(new JournalHeader(1, "C:\\Università\\caffè\n\"日本\".pdf", Fingerprint)),
            JournalCodec.SerializeEntry(new AddEntry(0, [RichStroke(), Stroke(10)])),
            JournalCodec.SerializeEntry(new EraseEntry(0, [Guid.NewGuid()], [new IndexedStroke(0, RichStroke())])),
            JournalCodec.SerializeEntry(new ViewEntry(1, 1, 0, 0)),
            JournalCodec.SerializeEntry(new ViewEntry(2, 1, 0, 0)),
            JournalCodec.SerializeEntry(new ViewEntry(0, 1, 0, 0)),
        };

        foreach (var line in lines)
        {
            Assert.All(line, c => Assert.InRange(c, ' ', '~'));
        }

        var header = JournalCodec.ParseHeader(lines[0]);
        Assert.Equal("C:\\Università\\caffè\n\"日本\".pdf", header!.Path);
    }

    [Fact]
    public void Torn_last_line_is_dropped_and_flagged()
    {
        var first = JournalCodec.SerializeEntry(new AddEntry(0, [Stroke(10)]));
        var torn = JournalCodec.SerializeEntry(new AddEntry(0, [Stroke(20)]));

        var contents = JournalCodec.Parse([Header(), first, JournalCodec.SerializeEntry(new ViewEntry(1, 1, 0, 0)), torn[..(torn.Length / 2)]]);

        Assert.NotNull(contents.Header);
        Assert.Equal(2, contents.Entries.Count);
        Assert.IsType<AddEntry>(contents.Entries[0]);
        Assert.Equal(new ViewEntry(1, 1, 0, 0), contents.Entries[1]);
        Assert.True(contents.HadTornTail);
        Assert.Equal(0, contents.SkippedCount);
    }

    [Fact]
    public void Trailing_blank_lines_do_not_hide_a_torn_last_line()
    {
        var contents = JournalCodec.Parse([Header(), JournalCodec.SerializeEntry(new ViewEntry(1, 1, 0, 0)), "{\"k\":\"ad", "", "  "]);

        Assert.Single(contents.Entries);
        Assert.True(contents.HadTornTail);
        Assert.Equal(0, contents.SkippedCount);
    }

    [Fact]
    public void Corrupt_middle_line_is_skipped_and_counted()
    {
        var contents = JournalCodec.Parse(
        [
            Header(),
            JournalCodec.SerializeEntry(new ViewEntry(1, 1, 0, 0)),
            "{\"k\":\"add\",\"p\":0,\"s\":[{\"v\":1}]}",
            "",
            JournalCodec.SerializeEntry(new ViewEntry(2, 1, 0, 0)),
        ]);

        Assert.Equal(2, contents.Entries.Count);
        Assert.Equal(new ViewEntry(1, 1, 0, 0), contents.Entries[0]);
        Assert.Equal(new ViewEntry(2, 1, 0, 0), contents.Entries[1]);
        Assert.Equal(1, contents.SkippedCount);
        Assert.False(contents.HadTornTail);
    }

    [Fact]
    public void Empty_input_and_header_only_input_parse_to_no_entries()
    {
        var empty = JournalCodec.Parse([]);
        var blank = JournalCodec.Parse(["", "   "]);
        var headerOnly = JournalCodec.Parse([Header()]);

        Assert.Null(empty.Header);
        Assert.Empty(empty.Entries);
        Assert.False(empty.HadTornTail);
        Assert.Equal(0, empty.SkippedCount);
        Assert.Null(blank.Header);
        Assert.Empty(blank.Entries);
        Assert.NotNull(headerOnly.Header);
        Assert.Empty(headerOnly.Entries);
        Assert.False(headerOnly.HadTornTail);
        Assert.Equal(0, headerOnly.SkippedCount);
    }

    [Fact]
    public void Torn_header_as_the_only_line_is_a_torn_tail_without_header()
    {
        var header = Header();

        var contents = JournalCodec.Parse([header[..10]]);

        Assert.Null(contents.Header);
        Assert.Empty(contents.Entries);
        Assert.True(contents.HadTornTail);
        Assert.Equal(0, contents.SkippedCount);
    }

    [Fact]
    public void Missing_header_yields_no_entries_and_counts_every_line_as_skipped()
    {
        var contents = JournalCodec.Parse(
        [
            JournalCodec.SerializeEntry(new ViewEntry(1, 1, 0, 0)),
            JournalCodec.SerializeEntry(new ViewEntry(2, 1, 0, 0)),
        ]);

        Assert.Null(contents.Header);
        Assert.Empty(contents.Entries);
        Assert.Equal(2, contents.SkippedCount);
        Assert.False(contents.HadTornTail);
    }

    [Fact]
    public void Header_with_unknown_format_version_is_reported()
    {
        var future = JournalCodec.SerializeHeader(new JournalHeader(2, @"C:\docs\book.pdf", Fingerprint));

        var contents = JournalCodec.Parse([future, JournalCodec.SerializeEntry(new ViewEntry(1, 1, 0, 0)), "{\"k\":\"something-new\"}"]);

        Assert.Equal(2, contents.Header!.FormatVersion);
        Assert.False(contents.IsSupportedVersion);
        Assert.Empty(contents.Entries);
        Assert.Equal(2, contents.SkippedCount);
        Assert.True(JournalCodec.Parse([Header()]).IsSupportedVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"k\":\"hdr\",\"fv\":1,\"path\":\"a\",\"fp\":\"bad\"}")]
    [InlineData("{\"k\":\"nope\"}")]
    [InlineData("{\"k\":\"add\",\"p\":-1,\"s\":[]}")]
    [InlineData("{\"k\":\"add\",\"p\":1.5,\"s\":[]}")]
    [InlineData("{\"k\":\"add\",\"p\":0}")]
    [InlineData("{\"k\":\"add\",\"p\":0,\"s\":[1]}")]
    [InlineData("{\"k\":\"add\",\"p\":0,\"p\":1,\"s\":[]}")]
    [InlineData("{\"k\":\"erase\",\"p\":0,\"rm\":[\"not-a-guid\"],\"add\":[]}")]
    [InlineData("{\"k\":\"erase\",\"p\":0,\"rm\":[],\"add\":[{\"i\":-1,\"s\":{}}]}")]
    [InlineData("{\"k\":\"erase\",\"p\":0,\"rm\":[]}")]
    [InlineData("{\"k\":\"view\",\"p\":0,\"z\":0,\"x\":0,\"y\":0}")]
    [InlineData("{\"k\":\"view\",\"p\":0,\"z\":1,\"x\":0}")]
    [InlineData("{\"k\":\"view\",\"p\":0,\"z\":1,\"x\":0,\"y\":0} trailing")]
    [InlineData("{\"k\":\"undo\"}")] // symbolic undo/redo are not part of the format (Ruling 10)
    [InlineData("{\"k\":\"redo\"}")]
    public void Corrupt_entry_lines_parse_to_null(string line)
    {
        Assert.Null(JournalCodec.ParseEntry(line));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"k\":\"undo\"}")]
    [InlineData("{\"k\":\"hdr\",\"fv\":0,\"path\":\"a\",\"fp\":\"1:2:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}")]
    [InlineData("{\"k\":\"hdr\",\"fv\":1,\"path\":\"\",\"fp\":\"1:2:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}")]
    [InlineData("{\"k\":\"hdr\",\"fv\":1,\"fp\":\"1:2:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}")]
    public void Corrupt_header_lines_parse_to_null(string line)
    {
        Assert.Null(JournalCodec.ParseHeader(line));
    }

    [Fact]
    public void Entry_with_a_stroke_payload_of_a_newer_version_is_rejected()
    {
        var line = JournalCodec.SerializeEntry(new AddEntry(0, [Stroke(10)])).Replace("\"v\":1", "\"v\":2", StringComparison.Ordinal);

        Assert.Null(JournalCodec.ParseEntry(line));
    }

    [Fact]
    public void Serializing_a_non_finite_view_throws()
    {
        Assert.Throws<ArgumentException>(() => JournalCodec.SerializeEntry(new ViewEntry(0, double.NaN, 0, 0)));
        Assert.Throws<ArgumentException>(() => JournalCodec.SerializeEntry(new ViewEntry(0, 1, double.PositiveInfinity, 0)));
    }

    [Fact]
    public void Header_carries_the_fingerprint_token()
    {
        var fingerprint = new DocumentFingerprint(5, 6, new string('b', 64));
        var line = JournalCodec.SerializeHeader(new JournalHeader(1, "x.pdf", fingerprint));

        Assert.Contains(fingerprint.ToToken(), line, StringComparison.Ordinal);
        Assert.StartsWith("{\"k\":\"hdr\",\"fv\":1,", line, StringComparison.Ordinal);
    }

    [Fact]
    public void View_entry_fit_width_flag_round_trips_and_is_optional()
    {
        var fit = JournalCodec.ParseEntry(JournalCodec.SerializeEntry(new ViewEntry(3, 1.25, 306, -4, FitWidth: true)));
        var manual = JournalCodec.ParseEntry(JournalCodec.SerializeEntry(new ViewEntry(3, 1.25, 306, 40, FitWidth: false)));
        var legacy = JournalCodec.SerializeEntry(new ViewEntry(3, 1.25, 30, 40));

        Assert.Equal(new ViewEntry(3, 1.25, 306, -4, true), fit);
        Assert.Equal(new ViewEntry(3, 1.25, 306, 40, false), manual);
        Assert.DoesNotContain("\"f\"", legacy, StringComparison.Ordinal); // entries without the flag stay as before
        Assert.Equal(new ViewEntry(3, 1.25, 30, 40), JournalCodec.ParseEntry(legacy));
        Assert.Null(((ViewEntry)JournalCodec.ParseEntry(legacy)!).FitWidth);

        // A line written before the flag existed, and one with a malformed flag, read as "no flag".
        Assert.Equal(new ViewEntry(1, 2, 30, 40), JournalCodec.ParseEntry("{\"k\":\"view\",\"p\":1,\"z\":2,\"x\":30,\"y\":40}"));
        Assert.Equal(new ViewEntry(1, 2, 30, 40), JournalCodec.ParseEntry("{\"k\":\"view\",\"p\":1,\"z\":2,\"x\":30,\"y\":40,\"f\":1}"));
    }
}
