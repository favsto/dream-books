using System.Globalization;
using DreamBooks.App.Resources;

namespace DreamBooks.App.Services;

/// <summary>Italian words for how long ago a document was opened ("ieri", "3 giorni fa"): the reader's local calendar days, never negative.</summary>
internal static class RelativeTimeFormatter
{
    private const int _daysPerWeek = 7;
    private const int _daysPerMonth = 30;

    public static string Format(DateTime openedUtc, DateTime nowUtc)
    {
        var days = Math.Max(0, (nowUtc.ToLocalTime().Date - openedUtc.ToLocalTime().Date).Days); // the reader's days, not UTC days
        if (days == 0)
        {
            return Strings.WhenToday;
        }

        if (days == 1)
        {
            return Strings.WhenYesterday;
        }

        if (days < _daysPerWeek)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.WhenDaysFormat, days);
        }

        if (days < _daysPerMonth)
        {
            var weeks = days / _daysPerWeek;
            return weeks == 1 ? Strings.WhenWeekAgo : string.Format(CultureInfo.CurrentCulture, Strings.WhenWeeksFormat, weeks);
        }

        var months = days / _daysPerMonth;
        return months == 1 ? Strings.WhenMonthAgo : string.Format(CultureInfo.CurrentCulture, Strings.WhenMonthsFormat, months);
    }
}
