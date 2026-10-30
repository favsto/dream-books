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
    public void Pdf_file_filter_keeps_the_pattern_part()
    {
        Assert.EndsWith("|*.pdf", Strings.PdfFileFilter, StringComparison.Ordinal);
    }
}
