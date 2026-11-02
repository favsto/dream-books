using System.Text;
using System.Text.Json;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Core.Settings;

namespace DreamBooks.Pdf.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "db-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (File.Exists(_root))
            {
                File.Delete(_root);
            }
            else
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string SettingsFile => Path.Combine(_root, "settings.json");

    private AppSettingsStore NewStore() => new(_root);

    private void WriteRaw(string json)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsFile, json);
    }

    private static AppSettings Everything() => new()
    {
        ThemeMode = ThemeMode.Dark,
        AccentOverride = "#12AB34",
        EraserMode = EraserMode.Partial,
        PressureCurve = PressureCurve.Soft,
        Smoothing = SmoothingLevel.Light,
        ToolbarFixed = true,
        ToolbarEdge = ToolbarEdge.Left,
        PenColor = InkColor.FromRgb(0xD3, 0x2F, 0x2F),
        PenWidthMm = 1.75,
        HighlighterColor = HighlighterPresets.Pink,
        HighlighterChisel = true,
        HighlighterAngleDeg = 120.5,
        HighlighterStraightLine = true,
        EraserSizeMm = 11.25,
        CustomPalette = CustomPalette.From(["#111111", "#ABCDEF", "#00FF00"]),
    };

    [Fact]
    public void Save_then_Load_round_trips_every_key()
    {
        var store = NewStore();
        var settings = Everything();

        store.Save(settings);

        Assert.Equal(settings, store.Load());
    }

    [Fact]
    public void Defaults_round_trip()
    {
        var store = NewStore();
        store.Save(AppSettings.Default);

        Assert.Equal(AppSettings.Default, store.Load());
    }

    [Fact]
    public void Missing_file_and_folder_load_defaults()
    {
        Assert.Equal(AppSettings.Default, NewStore().Load());
    }

    [Fact]
    public void Corrupt_json_loads_defaults()
    {
        var store = NewStore();
        WriteRaw("{\"v\":1,\"theme\":");
        Assert.Equal(AppSettings.Default, store.Load());

        File.WriteAllBytes(SettingsFile, [0, 1, 2, 255]);
        Assert.Equal(AppSettings.Default, store.Load());

        WriteRaw("[1,2,3]");
        Assert.Equal(AppSettings.Default, store.Load());
    }

    [Theory]
    [InlineData("{\"v\":2,\"theme\":\"Dark\"}")]
    [InlineData("{\"theme\":\"Dark\"}")]
    [InlineData("{\"v\":\"1\",\"theme\":\"Dark\"}")]
    public void Unknown_or_missing_version_loads_all_defaults(string json)
    {
        WriteRaw(json);

        Assert.Equal(AppSettings.Default, NewStore().Load());
    }

    [Theory]
    [InlineData("theme", "7")]
    [InlineData("theme", "\"purple\"")]
    [InlineData("accent", "\"red\"")]
    [InlineData("accent", "12")]
    [InlineData("eraserMode", "\"nope\"")]
    [InlineData("curve", "\"nope\"")]
    [InlineData("smoothing", "null")]
    [InlineData("toolbarFixed", "\"yes\"")]
    [InlineData("toolbarEdge", "\"middle\"")]
    [InlineData("penColor", "\"#12\"")]
    [InlineData("penWidthMm", "\"NaN\"")]
    [InlineData("penWidthMm", "0")]
    [InlineData("penWidthMm", "99")]
    [InlineData("penWidthMm", "1e999")]
    [InlineData("highlighterColor", "\"zzz\"")]
    [InlineData("highlighterChisel", "1")]
    [InlineData("highlighterAngleDeg", "181")]
    [InlineData("highlighterAngleDeg", "-1")]
    [InlineData("highlighterStraightLine", "\"x\"")]
    [InlineData("eraserSizeMm", "1")]
    [InlineData("eraserSizeMm", "21")]
    [InlineData("palette", "\"#111111\"")]
    public void Each_bad_value_falls_back_to_its_default_and_keeps_the_others(string key, string badValue)
    {
        var store = NewStore();
        store.Save(Everything());
        using var doc = JsonDocument.Parse(File.ReadAllText(SettingsFile));
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            sb.Append(first ? "" : ",").Append('"').Append(p.Name).Append("\":")
                .Append(p.Name == key ? badValue : p.Value.GetRawText());
            first = false;
        }

        WriteRaw(sb.Append('}').ToString());

        var loaded = store.Load();
        var d = AppSettings.Default;
        var expected = key switch
        {
            "theme" => Everything() with { ThemeMode = d.ThemeMode },
            "accent" => Everything() with { AccentOverride = d.AccentOverride },
            "eraserMode" => Everything() with { EraserMode = d.EraserMode },
            "curve" => Everything() with { PressureCurve = d.PressureCurve },
            "smoothing" => Everything() with { Smoothing = d.Smoothing },
            "toolbarFixed" => Everything() with { ToolbarFixed = d.ToolbarFixed },
            "toolbarEdge" => Everything() with { ToolbarEdge = d.ToolbarEdge },
            "penColor" => Everything() with { PenColor = d.PenColor },
            "penWidthMm" => Everything() with { PenWidthMm = d.PenWidthMm },
            "highlighterColor" => Everything() with { HighlighterColor = d.HighlighterColor },
            "highlighterChisel" => Everything() with { HighlighterChisel = d.HighlighterChisel },
            "highlighterAngleDeg" => Everything() with { HighlighterAngleDeg = d.HighlighterAngleDeg },
            "highlighterStraightLine" => Everything() with { HighlighterStraightLine = d.HighlighterStraightLine },
            "eraserSizeMm" => Everything() with { EraserSizeMm = d.EraserSizeMm },
            "palette" => Everything() with { CustomPalette = d.CustomPalette },
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
        Assert.Equal(expected, loaded);
    }

    [Fact]
    public void Unknown_extra_keys_are_ignored_and_missing_keys_default()
    {
        WriteRaw("{\"v\":1,\"future\":{\"a\":[1,2]},\"theme\":\"light\"}");

        Assert.Equal(AppSettings.Default with { ThemeMode = ThemeMode.Light }, NewStore().Load());
    }

    [Fact]
    public void Palette_entries_are_cleaned_when_read()
    {
        WriteRaw("{\"v\":1,\"palette\":[\"#abcdef\",\"junk\",3,\"#ABCDEF\",\"#000001\"]}");

        var loaded = NewStore().Load();

        Assert.Equal(["#ABCDEF", "#000001"], loaded.CustomPalette.Colors);
    }

    [Fact]
    public void A_palette_longer_than_twelve_keeps_the_newest_twelve()
    {
        var colors = string.Join(",", Enumerable.Range(0, 15).Select(i => $"\"#0000{i:X2}\""));
        WriteRaw("{\"v\":1,\"palette\":[" + colors + "]}");

        var palette = NewStore().Load().CustomPalette;

        Assert.Equal(12, palette.Count);
        Assert.Equal("#000003", palette.Colors[0]);
    }

    [Fact]
    public void The_highlighter_colour_always_carries_the_highlighter_alpha()
    {
        var store = NewStore();
        store.Save(AppSettings.Default with { HighlighterColor = new InkColor(255, 1, 2, 3) });

        Assert.Equal(new InkColor(HighlighterPresets.Alpha, 1, 2, 3), store.Load().HighlighterColor);
    }

    [Fact]
    public void Save_never_writes_NaN_or_out_of_range_numbers()
    {
        var store = NewStore();
        store.Save(AppSettings.Default with { PenWidthMm = double.NaN, EraserSizeMm = 500, HighlighterAngleDeg = -5 });

        var loaded = store.Load();
        Assert.Equal(AppSettings.DefaultPenWidthMm, loaded.PenWidthMm);
        Assert.Equal(AppSettings.MaxEraserSizeMm, loaded.EraserSizeMm);
        Assert.Equal(AppSettings.MinHighlighterAngleDeg, loaded.HighlighterAngleDeg);
    }

    [Fact]
    public async Task Save_is_atomic_a_reader_never_sees_a_torn_file()
    {
        var store = NewStore();
        store.Save(AppSettings.Default);
        var done = false;
        var torn = 0;
        var failures = new List<Exception>();
        store.Failed += (_, ex) => failures.Add(ex);

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!Volatile.Read(ref done))
            {
                try
                {
                    using var stream = new FileStream(SettingsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    var text = reader.ReadToEnd();
                    if (!text.StartsWith('{') || !text.EndsWith('}'))
                    {
                        Interlocked.Increment(ref torn);
                    }
                }
                catch (IOException)
                {
                }
            }
        })).ToArray();

        for (var i = 1; i <= 300; i++)
        {
            store.Save(AppSettings.Default with { PenWidthMm = 0.25 + i * 0.01 });
        }

        Volatile.Write(ref done, true);
        await Task.WhenAll(readers);

        Assert.Equal(0, torn);
        Assert.Empty(failures);
        Assert.Equal(0.25 + 300 * 0.01, store.Load().PenWidthMm, 6);
    }

    [Fact]
    public void Save_failure_raises_Failed_and_does_not_throw()
    {
        File.WriteAllText(_root, "this is a file where the folder should be");
        var store = NewStore();
        Exception? reported = null;
        store.Failed += (_, ex) => reported = ex;

        store.Save(Everything());

        Assert.NotNull(reported);
        Assert.DoesNotContain(_root, reported.Message, StringComparison.Ordinal);
        Assert.Equal(AppSettings.Default, store.Load());
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind_on_success()
    {
        var store = NewStore();
        store.Save(Everything());
        store.Save(Everything() with { ThemeMode = ThemeMode.Light });

        Assert.Equal([SettingsFile], Directory.GetFiles(_root));
    }

    [Fact]
    public void The_file_is_ascii_and_holds_no_path_or_text()
    {
        var store = NewStore();
        store.Save(Everything());

        var bytes = File.ReadAllBytes(SettingsFile);
        Assert.All(bytes, b => Assert.True(b < 128));
        var text = Encoding.ASCII.GetString(bytes);
        Assert.Contains("\"v\":1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("C:", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".pdf", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultRootDirectory_is_DreamBooks_under_LocalAppData()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DreamBooks");

        Assert.Equal(expected, AppSettingsStore.DefaultRootDirectory());
    }
}
