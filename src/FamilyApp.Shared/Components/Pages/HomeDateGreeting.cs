using System.Globalization;

namespace FamilyApp.Shared.Components.Pages;

public static class HomeDateGreeting
{
    private static readonly CultureInfo WeekdayCulture = CultureInfo.GetCultureInfo("en-US");

    public static string GetWeekdayName(DateTime localDateTime) => localDateTime.ToString("dddd", WeekdayCulture);

    public static string GetGreeting(DateTime localDateTime)
    {
        if (localDateTime.Hour < 12)
        {
            return "Good morning.";
        }

        if (localDateTime.Hour < 17)
        {
            return "Good afternoon.";
        }

        return "Good evening.";
    }

    public static TimeSpan GetNextRefreshDelay(DateTime localDateTime)
    {
        var currentMinute = new DateTime(
            localDateTime.Year,
            localDateTime.Month,
            localDateTime.Day,
            localDateTime.Hour,
            localDateTime.Minute,
            0,
            localDateTime.Kind);

        var delay = currentMinute.AddMinutes(1) - localDateTime;
        return delay <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : delay;
    }
}
