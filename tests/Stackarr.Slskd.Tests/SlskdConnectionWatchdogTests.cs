using Stackarr.Slskd;

namespace Stackarr.Slskd.Tests;

/// <summary>
/// Covers the Soulseek reconnect watchdog's decision logic: restart only after slskd has been
/// disconnected for the threshold, never within the cooldown, and clear the timer when it recovers.
/// </summary>
public class SlskdConnectionWatchdogTests
{
    static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task RestartsOnlyAfterTheThreshold_AndHonoursTheCooldown()
    {
        SlskdConnectionWatchdog watchdog = new();

        await Assert.That(watchdog.ShouldRestart(available: false, T0)).IsFalse();                 // just went down
        await Assert.That(watchdog.ShouldRestart(false, T0.AddMinutes(1))).IsFalse();             // < 2 min
        await Assert.That(watchdog.ShouldRestart(false, T0.AddMinutes(2))).IsTrue();              // threshold reached

        watchdog.RecordRestart(T0.AddMinutes(2));
        await Assert.That(watchdog.ShouldRestart(false, T0.AddMinutes(3))).IsFalse();             // within cooldown
        await Assert.That(watchdog.ShouldRestart(false, T0.AddMinutes(4))).IsTrue();              // threshold + cooldown passed
    }

    [Test]
    public async Task AHealthyCheck_ClearsTheDisconnectedTimer()
    {
        SlskdConnectionWatchdog watchdog = new();

        await Assert.That(watchdog.ShouldRestart(false, T0)).IsFalse();
        await Assert.That(watchdog.ShouldRestart(true, T0.AddMinutes(5))).IsFalse();              // recovered
        await Assert.That(watchdog.ShouldRestart(false, T0.AddMinutes(6))).IsFalse();             // timer restarted
        await Assert.That(watchdog.ShouldRestart(false, T0.AddMinutes(8))).IsTrue();
    }
}
