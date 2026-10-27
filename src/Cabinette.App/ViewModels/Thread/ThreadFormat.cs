using System.Globalization;
using Cabinette.App.Localization;

namespace Cabinette.App.ViewModels.Thread;

/// <summary>Text formatting for the thread view (sizes, durations, date headers). All patterns come from ThreadStrings.resx.</summary>
public static class ThreadFormat
{
    public static string Size(long? bytes)
    {
        if (bytes is not { } b || b < 0)
        {
            return "";
        }

        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
        return b switch
        {
            < 1024 => L.Format("Thread_SizeBytes", b),
            < (long)Mb => L.Format("Thread_SizeKB", Math.Max(1, b / Kb)),
            < (long)Gb => L.Format("Thread_SizeMB", b / Mb),
            _ => L.Format("Thread_SizeGB", b / Gb),
        };
    }

    public static string Duration(TimeSpan? duration)
    {
        if (duration is not { } d || d < TimeSpan.Zero)
        {
            return "";
        }

        return d.TotalHours >= 1
            ? string.Format(CultureInfo.CurrentCulture, "{0}:{1:00}:{2:00}", (int)d.TotalHours, d.Minutes, d.Seconds)
            : string.Format(CultureInfo.CurrentCulture, "{0}:{1:00}", (int)d.TotalMinutes, d.Seconds);
    }

    public static string Duration(double seconds) => Duration(TimeSpan.FromSeconds(Math.Max(0, seconds)));

    public static string Time(DateTimeOffset value)
        => value.ToLocalTime().ToString(L.Get("Thread_TimeFormat"), CultureInfo.CurrentCulture);

    public static string DateTimeShort(DateTimeOffset value)
        => value.ToLocalTime().ToString(L.Get("Thread_DateTimeFormat"), CultureInfo.CurrentCulture);

    /// <summary>Like <see cref="DateTimeShort"/>, but the time as it was where it was taken (a photo's date taken keeps its offset).</summary>
    public static string WallClockShort(DateTimeOffset value)
        => value.DateTime.ToString(L.Get("Thread_DateTimeFormat"), CultureInfo.CurrentCulture);

    /// <summary>"Today", "Yesterday", a weekday within the last week, otherwise a long date.</summary>
    public static string DateHeader(DateTime date, DateTime today)
    {
        var days = (today.Date - date.Date).TotalDays;
        if (days == 0)
        {
            return L.Get("Thread_Today");
        }

        if (days == 1)
        {
            return L.Get("Thread_Yesterday");
        }

        if (days is > 1 and < 7)
        {
            return CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(date.DayOfWeek);
        }

        return date.ToString(L.Get("Thread_LongDateFormat"), CultureInfo.CurrentCulture);
    }

    /// <summary>Month group header for the grid ("This month", "September 2026").</summary>
    public static string MonthHeader(DateTime month, DateTime today)
        => month.Year == today.Year && month.Month == today.Month
            ? L.Get("Thread_ThisMonth")
            : month.ToString(L.Get("Thread_MonthFormat"), CultureInfo.CurrentCulture);

    /// <summary>A file URI that survives '#' and '%' in names (new Uri(path) would treat them as fragment/escapes).</summary>
    public static Uri FileUri(string path)
    {
        if (path.IndexOfAny(['#', '%']) < 0)
        {
            return new Uri(path, UriKind.Absolute);
        }

        return new UriBuilder { Scheme = Uri.UriSchemeFile, Host = "", Path = path.Replace('\\', '/') }.Uri;
    }

    public static string FileCount(int count)
        => count == 1 ? L.Get("Thread_FileCountOne") : L.Format("Thread_FileCountMany", count);
}
