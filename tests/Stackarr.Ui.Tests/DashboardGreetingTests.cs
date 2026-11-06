using Stackarr.Ui;

namespace Stackarr.Ui.Tests;

/// <summary>
/// The dashboard greeting follows the user's local hour. These cases pin every boundary so the
/// wording cannot drift when the ranges are edited.
/// </summary>
public class DashboardGreetingTests
{
    [Test]
    [Arguments(6, "Good morning")]
    [Arguments(11, "Good morning")]
    [Arguments(12, "Good afternoon")]
    [Arguments(15, "Good afternoon")]
    [Arguments(16, "Good evening")]
    [Arguments(21, "Good evening")]
    [Arguments(22, "It's getting late")]
    [Arguments(0, "It's getting late")]
    [Arguments(1, "We have ourselves a night owl")]
    [Arguments(3, "We have ourselves a night owl")]
    [Arguments(4, "I hope you slept")]
    [Arguments(5, "I hope you slept")]
    public async Task ForHour_UsesTheExpectedBoundary(int hour, string expected)
    {
        await Assert.That(DashboardGreeting.ForHour(hour)).IsEqualTo(expected);
    }

    [Test]
    public async Task For_AppendsTheUsername()
    {
        await Assert.That(DashboardGreeting.For(9, "Alex")).IsEqualTo("Good morning, Alex");
        await Assert.That(DashboardGreeting.For(9, "  ")).IsEqualTo("Good morning");
    }
}
