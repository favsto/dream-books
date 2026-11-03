using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using DreamBooks.App.Resources;

namespace DreamBooks.App.Tests;

public class StringsTests
{
    private static readonly Type _stringsType = typeof(Strings);

    private static Dictionary<string, string> ResxEntries()
    {
        var manager = new ResourceManager("DreamBooks.App.Resources.Strings", _stringsType.Assembly);
        var set = manager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static List<PropertyInfo> DesignerProperties() =>
        _stringsType.GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string))
            .ToList();

    [Fact]
    public void Every_resx_key_has_a_designer_property_that_resolves()
    {
        foreach (var (key, expected) in ResxEntries())
        {
            var property = _stringsType.GetProperty(key, BindingFlags.Public | BindingFlags.Static);
            Assert.True(property is not null, $"Strings.Designer.cs is missing '{key}'.");
            var value = (string?)property.GetValue(null);
            Assert.False(string.IsNullOrEmpty(value), $"'{key}' is empty.");
            Assert.Equal(expected, value); // a lookup miss would return the key name instead
        }
    }

    [Fact]
    public void Every_designer_property_has_a_resx_key()
    {
        var keys = ResxEntries().Keys;
        foreach (var property in DesignerProperties())
        {
            Assert.True(keys.Contains(property.Name), $"Strings.resx is missing '{property.Name}'.");
        }
    }

    [Fact]
    public void Strings_Designer_stays_in_sync()
    {
        // The marks panel's texts (FR-103, UI-04) are in both files and the formats take their arguments.
        var keys = ResxEntries();
        foreach (var key in new[] { "ClosePanel", "OpenMarksList", "MarksListLabel", "MarksScanning", "MarksCountNameFormat", "MarkRowNameFormat", "MarkSideLeft", "MarkSideRight", "MarkSideCentre" })
        {
            Assert.True(keys.ContainsKey(key), $"Strings.resx is missing '{key}'.");
            Assert.NotNull(_stringsType.GetProperty(key, BindingFlags.Public | BindingFlags.Static));
        }

        var name = string.Format(CultureInfo.CurrentCulture, Strings.MarkRowNameFormat, 12, Strings.MarkSideLeft);
        Assert.Contains("12", name, StringComparison.Ordinal);
        Assert.Contains(Strings.MarkSideLeft, name, StringComparison.Ordinal);
        Assert.DoesNotContain("{2}", Strings.MarkRowNameFormat, StringComparison.Ordinal); // no line number (fixed default lines)

        // The Pages tab's texts (FR-103, UI-04), in both files; the row name takes the page number.
        foreach (var key in new[] { "PagesTab", "MarksTabName", "PagesTabName", "PagesListLabel", "OpenPagesList", "PagesListButton", "PageRowNameFormat", "PageRowCurrent", "PageRowHasMarks", "PageRowHasNotes" })
        {
            Assert.True(keys.ContainsKey(key), $"Strings.resx is missing '{key}'.");
            Assert.NotNull(_stringsType.GetProperty(key, BindingFlags.Public | BindingFlags.Static));
        }

        Assert.Contains("37", string.Format(CultureInfo.CurrentCulture, Strings.PageRowNameFormat, 37), StringComparison.Ordinal);

        // The highlighter tool and its options (FR-41, FR-44, FR-46), in both files.
        foreach (var key in new[]
                 {
                     "ToolHighlighter", "HighlighterOptions", "HighlighterTipRound", "HighlighterTipChisel", "HighlighterAngle",
                     "HighlighterStraightLine", "HighlighterYellow", "HighlighterGreen", "HighlighterLightBlue",
                     "HighlighterPink", "HighlighterOrange", "HighlighterPurple",
                 })
        {
            Assert.True(keys.ContainsKey(key), $"Strings.resx is missing '{key}'.");
            Assert.NotNull(_stringsType.GetProperty(key, BindingFlags.Public | BindingFlags.Static));
        }

        // The toolbar and its option panels (UI-02, FR-25, FR-31, FR-110), in both files.
        foreach (var key in new[]
                 {
                     "ToolSelection", "ToolBookmark", "PenBlack", "PenDarkBlue", "PenBlue", "PenRed", "PenGreen", "PenOrange",
                     "PenPurple", "PenGrey", "PenOptions", "EraserOptions", "ClearPage", "ClearPageConfirm", "ClearPageConfirmYes",
                     "Cancel", "AddCustomColor", "RemoveCustomColor", "WarnSettingsNotSaved", "SettingsWarningLabel",
                 })
        {
            Assert.True(keys.ContainsKey(key), $"Strings.resx is missing '{key}'.");
            Assert.NotNull(_stringsType.GetProperty(key, BindingFlags.Public | BindingFlags.Static));
        }

        // The main menu, the title bar's name and the notices (UI-03, UI-06, FR-84, phase 4a task 5), in both files; the
        // debug panel's own texts went with it.
        foreach (var key in new[]
                 {
                     "MainMenu", "MainMenuPanel", "MenuOpen", "MenuSaveAs", "MenuRecentFiles", "MenuExport", "MenuSearchName",
                     "MenuSettings", "MenuNotYetAvailable", "ShortcutOpen", "ShortcutSave", "ShortcutSaveAs", "ShortcutMarks",
                     "ShortcutPages", "ShortcutSearch", "DocumentTitleDirtyFormat", "NoticeBarLabel", "CloseNotice",
                 })
        {
            Assert.True(keys.ContainsKey(key), $"Strings.resx is missing '{key}'.");
            Assert.NotNull(_stringsType.GetProperty(key, BindingFlags.Public | BindingFlags.Static));
        }

        foreach (var gone in new[] { "Panel", "Zoom", "GoToPage", "ClearSearch" }) // only the debug panel used them
        {
            Assert.False(keys.ContainsKey(gone), gone);
        }
        Assert.Equal("a.pdf *", string.Format(CultureInfo.CurrentCulture, Strings.DocumentTitleDirtyFormat, "a.pdf"));
    }

    [Fact]
    public void Pdf_file_filter_keeps_the_pattern_part()
    {
        Assert.EndsWith("|*.pdf", Strings.PdfFileFilter, StringComparison.Ordinal);
    }
}
