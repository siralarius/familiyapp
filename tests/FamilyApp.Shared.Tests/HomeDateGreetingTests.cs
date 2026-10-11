using FamilyApp.Shared.Components.Pages;
using Xunit;

namespace FamilyApp.Shared.Tests;

public class HomeDateGreetingTests
{
    [Fact]
    public void Returns_expected_day_and_greeting_for_sunday_evening_example()
    {
        var localDateTime = new DateTime(2026, 10, 11, 19, 41, 0);

        Assert.Equal("Sunday", HomeDateGreeting.GetWeekdayName(localDateTime));
        Assert.Equal("Good evening.", HomeDateGreeting.GetGreeting(localDateTime));
    }

    [Fact]
    public void Returns_correct_greeting_across_defined_boundaries()
    {
        Assert.Equal("Good morning.", HomeDateGreeting.GetGreeting(new DateTime(2026, 10, 11, 11, 59, 59)));
        Assert.Equal("Good afternoon.", HomeDateGreeting.GetGreeting(new DateTime(2026, 10, 11, 12, 0, 0)));
        Assert.Equal("Good afternoon.", HomeDateGreeting.GetGreeting(new DateTime(2026, 10, 11, 16, 59, 59)));
        Assert.Equal("Good evening.", HomeDateGreeting.GetGreeting(new DateTime(2026, 10, 11, 17, 0, 0)));
    }

    [Fact]
    public void Returns_correct_weekday_for_entire_week()
    {
        var weekStart = new DateTime(2026, 10, 11, 8, 0, 0);
        var expectedDays = new[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

        for (var i = 0; i < expectedDays.Length; i++)
        {
            Assert.Equal(expectedDays[i], HomeDateGreeting.GetWeekdayName(weekStart.AddDays(i)));
        }
    }

    [Fact]
    public void Returns_delay_until_next_minute_boundary()
    {
        var localDateTime = new DateTime(2026, 10, 11, 23, 59, 40, 500);

        Assert.Equal(TimeSpan.FromMilliseconds(19500), HomeDateGreeting.GetNextRefreshDelay(localDateTime));
    }
}
