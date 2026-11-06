namespace Arrk.Ui;

/// <summary>
/// The time-of-day greeting shown under the dashboard title. The hour comes from the browser, so
/// the wording follows the signed-in user's local clock rather than the server's time zone.
/// </summary>
public static class DashboardGreeting
{
    public static string ForHour(int hour) => hour switch
    {
        >= 6 and < 12 => "Good morning",
        >= 12 and < 16 => "Good afternoon",
        >= 16 and < 22 => "Good evening",
        >= 22 or < 1 => "It's getting late",
        >= 1 and < 4 => "We have ourselves a night owl",
        _ => "I hope you slept"
    };

    public static string For(int hour, string? username) =>
        string.IsNullOrWhiteSpace(username)
            ? ForHour(hour)
            : $"{ForHour(hour)}, {username.Trim()}";
}
