using DreamBooks.App.Services;

namespace DreamBooks.App.Tests;

/// <summary>"When did I last open this?" in Italian words for the start screen's covers (FR-03).</summary>
public sealed class RelativeTimeFormatterTests
{
    private static readonly DateTime _now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "oggi")]
    [InlineData(0.5, "oggi")]
    [InlineData(1, "ieri")]
    [InlineData(2, "2 giorni fa")]
    [InlineData(3, "3 giorni fa")]
    [InlineData(6, "6 giorni fa")]
    [InlineData(7, "1 settimana fa")]
    [InlineData(14, "2 settimane fa")]
    [InlineData(29, "4 settimane fa")]
    [InlineData(30, "1 mese fa")]
    [InlineData(60, "2 mesi fa")]
    [InlineData(400, "13 mesi fa")]
    [InlineData(-3, "oggi")] // a date in the future (clock changed): never negative
    public void Words_for_the_days_since_the_file_was_opened(double daysAgo, string expected)
    {
        Assert.Equal(expected, RelativeTimeFormatter.Format(_now.AddDays(-daysAgo), _now));
    }

    [Fact]
    public void Days_are_the_readers_local_days_not_UTC_days()
    {
        // Local 01:00 and the 23:00 before it: two local days, whatever the time zone of the machine.
        var localNow = new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Local);
        var earlierSameLocalDay = localNow.AddMinutes(-30);
        var previousLocalDay = localNow.AddHours(-2);

        Assert.Equal("oggi", RelativeTimeFormatter.Format(earlierSameLocalDay.ToUniversalTime(), localNow.ToUniversalTime()));
        Assert.Equal("ieri", RelativeTimeFormatter.Format(previousLocalDay.ToUniversalTime(), localNow.ToUniversalTime()));
    }

    [Fact]
    public void Yesterday_is_a_calendar_day_not_24_hours()
    {
        var lateYesterday = new DateTime(2026, 11, 19, 23, 50, 0, DateTimeKind.Local).ToUniversalTime();
        var earlyToday = new DateTime(2026, 11, 20, 0, 10, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal("ieri", RelativeTimeFormatter.Format(lateYesterday, earlyToday));
    }
}
