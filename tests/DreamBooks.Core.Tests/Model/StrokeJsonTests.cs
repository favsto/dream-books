using System.Text;
using System.Text.Json;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Model;

public class StrokeJsonTests
{
    private const string _head = "{\"v\":1,\"id\":\"0f8fad5bd9cb469fa16570867728950e\",\"tool\":\"pen\",\"color\":\"#FF000000\",";
    private const string _goodPts = "\"pts\":[{\"x\":1,\"y\":2,\"t\":0}]}";

    private static readonly Guid _id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    private static InkStroke Make(params InkPoint[] points)
        => new(_id, InkTool.Highlighter, new InkColor(0x80, 0xFF, 0xEE, 0x00), 12.5, points);

    private static InkStroke Parsed(string json)
    {
        var result = StrokeJson.Parse(json);
        Assert.Equal(StrokeParseStatus.Ok, result.Status);
        return result.Stroke!;
    }

    [Fact]
    public void Serialize_then_Parse_round_trips_every_field_of_a_stroke()
    {
        var stroke = Make(
            new InkPoint(10.25, 20.5, 0.75f, -0.3f, 0.2f, 1.1f, 123456789012L),
            new InkPoint(11, 21, null, null, null, null, 0),
            new InkPoint(12, 22, 0.1f, null, 0.5f, null, 5));

        var json = StrokeJson.Serialize(stroke);
        var back = Parsed(json);

        Assert.Equal(stroke.Id, back.Id);
        Assert.Equal(stroke.Tool, back.Tool);
        Assert.Equal(stroke.Color, back.Color);
        Assert.Equal(stroke.BaseWidthPt, back.BaseWidthPt);
        Assert.Equal(stroke.Points, back.Points);
        Assert.Null(back.Points[1].Pressure);
        Assert.Contains("{\"x\":11,\"y\":21,\"t\":0}", json);
        Assert.Contains("\"id\":\"0f8fad5bd9cb469fa16570867728950e\"", json);
        Assert.Contains("\"tool\":\"highlighter\"", json);
        Assert.Contains("\"color\":\"#80FFEE00\"", json);
    }

    [Fact]
    public void Pen_tool_is_written_as_pen()
    {
        var stroke = new InkStroke(_id, InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1, [new InkPoint(0, 0, null, null, null, null, 0)]);
        Assert.Contains("\"tool\":\"pen\"", StrokeJson.Serialize(stroke));
    }

    [Fact]
    public void Serialized_json_is_ascii_only_and_starts_with_version_1()
    {
        var json = StrokeJson.Serialize(Make(new InkPoint(1, 2, 0.5f, null, null, null, 3)));

        Assert.StartsWith("{\"v\":1,", json);
        Assert.All(json, c => Assert.InRange(c, ' ', '~'));
    }

    [Fact]
    public void Escaping_helper_turns_non_ascii_into_unicode_escapes_and_parses_back()
    {
        var escaped = StrokeJson.EscapeString("è€");

        Assert.Equal("\"\\u00E8\\u20AC\"", escaped, ignoreCase: true);
        Assert.All(escaped, c => Assert.InRange(c, ' ', '~'));
        Assert.Equal("è€", JsonSerializer.Deserialize<string>(escaped));
    }

    [Fact]
    public void Doubles_round_trip_exactly()
    {
        double[] values = [0.1, 1e-7, 123456.789012345, double.Epsilon, double.MaxValue, 1.0000000000000002, 0.30000000000000004];
        var points = values.Select(v => new InkPoint(v, -v, null, null, null, null, 0)).ToArray();
        var stroke = new InkStroke(_id, InkTool.Pen, InkColor.FromRgb(1, 2, 3), 1e-7, points);

        var back = Parsed(StrokeJson.Serialize(stroke));

        Assert.Equal(1e-7, back.BaseWidthPt);
        for (var i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], back.Points[i].X);
            Assert.Equal(-values[i], back.Points[i].Y);
        }
    }

    [Fact]
    public void Floats_round_trip_exactly()
    {
        float[] values = [0.1f, 1e-7f, 0.7071068f, float.Epsilon, float.MaxValue];
        var points = values.Select(v => new InkPoint(0, 0, v, v, -v, v, 0)).ToArray();

        var back = Parsed(StrokeJson.Serialize(Make(points)));

        for (var i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], back.Points[i].Pressure);
            Assert.Equal(values[i], back.Points[i].TiltX);
            Assert.Equal(-values[i], back.Points[i].TiltY);
            Assert.Equal(values[i], back.Points[i].TipAngle);
        }
    }

    [Fact]
    public void Serialize_rejects_non_finite_values()
    {
        Assert.Throws<ArgumentException>(() => StrokeJson.Serialize(Make(new InkPoint(double.NaN, 0, null, null, null, null, 0))));
        Assert.Throws<ArgumentException>(() => StrokeJson.Serialize(Make(new InkPoint(0, double.PositiveInfinity, null, null, null, null, 0))));
        Assert.Throws<ArgumentException>(() => StrokeJson.Serialize(Make(new InkPoint(0, 0, float.NaN, null, null, null, 0))));
    }

    [Fact]
    public void Parse_unknown_future_version_returns_UnsupportedVersion()
    {
        var json = StrokeJson.Serialize(Make(new InkPoint(1, 2, null, null, null, null, 0))).Replace("\"v\":1", "\"v\":2");
        var result = StrokeJson.Parse(json);

        Assert.Equal(StrokeParseStatus.UnsupportedVersion, result.Status);
        Assert.Null(result.Stroke);
        Assert.Equal(StrokeParseStatus.UnsupportedVersion, StrokeJson.Parse("{\"v\":3,\"pts\":\"whatever\"}").Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"v\":1,\"id\":\"0f8fad5bd9cb469fa16570867728950e\",\"tool\":\"pen\",\"color\":\"#FF000000\",\"w\":1,\"pts\":[{\"x\":1,")]
    [InlineData("{\"v\":1,\"id\":\"0f8fad5bd9cb469fa16570867728950e\",\"tool\":\"pen\",\"color\":\"#FF000000\",\"w\":\"1\",\"pts\":[{\"x\":1,\"y\":2,\"t\":0}]}")]
    [InlineData("{\"v\":1,\"id\":\"0f8fad5bd9cb469fa16570867728950e\",\"tool\":\"pen\",\"color\":\"#FF000000\",\"w\":1,\"pts\":[]}")]
    [InlineData("{\"v\":1,\"tool\":\"pen\",\"color\":\"#FF000000\",\"w\":1,\"pts\":[{\"x\":1,\"y\":2,\"t\":0}]}")]
    [InlineData("{\"v\":1,\"id\":\"0f8fad5bd9cb469fa16570867728950e\",\"tool\":\"pen\",\"color\":\"#FF000000\",\"w\":NaN,\"pts\":[{\"x\":1,\"y\":2,\"t\":0}]}")]
    [InlineData("{\"v\":1,\"id\":\"0f8fad5bd9cb469fa16570867728950e\",\"tool\":\"pen\",\"color\":\"#FF000000\",\"w\":Infinity,\"pts\":[{\"x\":1,\"y\":2,\"t\":0}]}")]
    [InlineData("{\"v\":1,\"id\":\"0f8fad5bd9cb469fa16570867728950e\",\"tool\":\"pen\",\"color\":\"#FF000000\",\"w\":0,\"pts\":[{\"x\":1,\"y\":2,\"t\":0}]}")]
    [InlineData("{\"v\":1,\"id\":\"0f8fad5bd9cb469fa16570867728950e\",\"tool\":\"brush\",\"color\":\"#FF000000\",\"w\":1,\"pts\":[{\"x\":1,\"y\":2,\"t\":0}]}")]
    [InlineData("{\"v\":1,\"id\":\"0f8fad5bd9cb469fa16570867728950e\",\"tool\":\"pen\",\"color\":\"red\",\"w\":1,\"pts\":[{\"x\":1,\"y\":2,\"t\":0}]}")]
    [InlineData("{\"v\":1,\"id\":\"nope\",\"tool\":\"pen\",\"color\":\"#FF000000\",\"w\":1,\"pts\":[{\"x\":1,\"y\":2,\"t\":0}]}")]
    [InlineData("{\"v\":1,\"id\":\"0f8fad5bd9cb469fa16570867728950e\",\"tool\":\"pen\",\"color\":\"#FF000000\",\"w\":1,\"pts\":[{\"y\":2,\"t\":0}]}")]
    [InlineData("{\"v\":1,\"id\":\"0f8fad5bd9cb469fa16570867728950e\",\"tool\":\"pen\",\"color\":\"#FF000000\",\"w\":1,\"pts\":[{\"x\":1,\"y\":2,\"t\":0,\"p\":\"x\"}]}")]
    [InlineData("{\"v\":1,\"v\":1,\"id\":\"0f8fad5bd9cb469fa16570867728950e\",\"tool\":\"pen\",\"color\":\"#FF000000\",\"w\":1,\"pts\":[{\"x\":1,\"y\":2,\"t\":0}]}")]
    [InlineData("{\"v\":0,\"id\":\"0f8fad5bd9cb469fa16570867728950e\",\"tool\":\"pen\",\"color\":\"#FF000000\",\"w\":1,\"pts\":[{\"x\":1,\"y\":2,\"t\":0}]}")]
    [InlineData("{\"id\":\"0f8fad5bd9cb469fa16570867728950e\",\"tool\":\"pen\",\"color\":\"#FF000000\",\"w\":1,\"pts\":[{\"x\":1,\"y\":2,\"t\":0}]}")]
    public void Parse_corrupt_or_truncated_json_returns_Corrupt(string json)
    {
        var result = StrokeJson.Parse(json);

        Assert.Equal(StrokeParseStatus.Corrupt, result.Status);
        Assert.Null(result.Stroke);
    }

    [Fact]
    public void Parse_null_or_any_truncation_of_a_real_payload_returns_Corrupt_without_throwing()
    {
        Assert.Equal(StrokeParseStatus.Corrupt, StrokeJson.Parse(null!).Status);
        var json = StrokeJson.Serialize(Make(new InkPoint(1, 2, 0.5f, 0.1f, 0.2f, 0.3f, 9)));
        for (var n = 0; n < json.Length; n++)
        {
            Assert.Equal(StrokeParseStatus.Corrupt, StrokeJson.Parse(json[..n]).Status);
        }
    }

    [Fact]
    public void Parse_tolerates_unknown_extra_fields()
    {
        const string json = "{\"v\":1,\"extra\":{\"a\":[1,2]},\"id\":\"0f8fad5bd9cb469fa16570867728950e\",\"tool\":\"pen\",\"color\":\"#FF000000\",\"w\":1,\"pts\":[{\"x\":1,\"y\":2,\"t\":0,\"zz\":true}]}";

        Assert.Equal(1, Parsed(json).Points[0].X);
    }

    [Fact]
    public void Parse_rejects_oversized_payloads()
    {
        var huge = new string(' ', (16 * 1024 * 1024) + 1);

        Assert.Equal(StrokeParseStatus.Corrupt, StrokeJson.Parse(huge).Status);
    }

    [Fact]
    public void Parse_rejects_more_than_one_million_points()
    {
        var sb = new StringBuilder(_head + "\"w\":1,\"pts\":[");
        for (var i = 0; i < 1_000_001; i++)
        {
            sb.Append(i == 0 ? "{}" : ",{}");
        }

        sb.Append("]}");

        Assert.Equal(StrokeParseStatus.Corrupt, StrokeJson.Parse(sb.ToString()).Status);
        Assert.Equal(StrokeParseStatus.Ok, StrokeJson.Parse(_head + "\"w\":1," + _goodPts).Status);
    }
}
