using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Events;

using Arrk.TestSupport;
namespace Arrk.DownloadManagement.Tests;

public class TorrentPenaltyTrackerTests
{
    static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static (TorrentPenaltyTracker Tracker, FakeEventStore Store, TestTimeProvider Time) Create()
    {
        FakeEventStore store = new();
        TestTimeProvider time = new(Start);
        TorrentPenaltyTracker tracker = new(store, time, NullLogger<TorrentPenaltyTracker>.Instance);
        return (tracker, store, time);
    }

    static PenaltyResult Accrue(
        TorrentPenaltyTracker tracker,
        TestTimeProvider time,
        string hash,
        double weightPerMinute = 1,
        double limitMinutes = 30,
        TorrentHealthReason reason = TorrentHealthReason.Slow,
        bool zeroProgress = false) =>
        tracker.Accrue(TestConfigs.Torrent(hash), reason, zeroProgress, time.Now.UtcDateTime, weightPerMinute, limitMinutes);

    [Test]
    public async Task Accrue_FirstTime_StartsAtZero()
    {
        (TorrentPenaltyTracker tracker, _, TestTimeProvider time) = Create();

        PenaltyResult result = Accrue(tracker, time, "h1");

        await Assert.That(result.FirstSighting).IsTrue();
        await Assert.That(result.PenaltyMinutes).IsEqualTo(0);
        await Assert.That(tracker.GetPenalties().Count).IsEqualTo(1);
    }

    [Test]
    public async Task Accrue_AddsElapsedTimesWeight()
    {
        (TorrentPenaltyTracker tracker, _, TestTimeProvider time) = Create();
        Accrue(tracker, time, "h1");

        time.AdvanceMinutes(10);
        PenaltyResult result = Accrue(tracker, time, "h1");

        await Assert.That(result.FirstSighting).IsFalse();
        await Assert.That(result.PenaltyMinutes).IsEqualTo(10);
    }

    [Test]
    public async Task Accrue_ZeroProgress_AppliesWeight()
    {
        (TorrentPenaltyTracker tracker, _, TestTimeProvider time) = Create();
        Accrue(tracker, time, "h1", weightPerMinute: 4, zeroProgress: true, reason: TorrentHealthReason.Stalled);

        time.AdvanceMinutes(5);
        PenaltyResult result = Accrue(tracker, time, "h1", weightPerMinute: 4, zeroProgress: true, reason: TorrentHealthReason.Stalled);

        await Assert.That(result.PenaltyMinutes).IsEqualTo(20);
    }

    [Test]
    public async Task Accrue_ReasonChange_KeepsPenalty()
    {
        (TorrentPenaltyTracker tracker, _, TestTimeProvider time) = Create();
        Accrue(tracker, time, "h1", reason: TorrentHealthReason.Slow);

        time.AdvanceMinutes(10);
        PenaltyResult result = Accrue(tracker, time, "h1", reason: TorrentHealthReason.Stalled);

        await Assert.That(result.Reason).IsEqualTo(TorrentHealthReason.Stalled);
        await Assert.That(result.PenaltyMinutes).IsEqualTo(10);
    }

    [Test]
    public async Task Decay_RemovesMinutesWhileHealthy()
    {
        (TorrentPenaltyTracker tracker, _, TestTimeProvider time) = Create();
        Accrue(tracker, time, "h1");
        time.AdvanceMinutes(10);
        Accrue(tracker, time, "h1");

        time.AdvanceMinutes(4);
        bool dropped = tracker.Decay("h1", time.Now.UtcDateTime, recoveryPerMinute: 0.5);

        await Assert.That(dropped).IsFalse();
        await Assert.That(tracker.GetPenalties()[0].PenaltyMinutes).IsEqualTo(8);
    }

    [Test]
    public async Task Decay_ToZero_DropsRecord()
    {
        (TorrentPenaltyTracker tracker, _, TestTimeProvider time) = Create();
        Accrue(tracker, time, "h1");
        time.AdvanceMinutes(10);
        Accrue(tracker, time, "h1");

        time.AdvanceMinutes(20);
        bool dropped = tracker.Decay("h1", time.Now.UtcDateTime, recoveryPerMinute: 0.5);

        await Assert.That(dropped).IsTrue();
        await Assert.That(tracker.GetPenalties()).IsEmpty();
    }

    [Test]
    public async Task Decay_UnknownTorrent_DoesNothing()
    {
        (TorrentPenaltyTracker tracker, _, TestTimeProvider time) = Create();

        bool dropped = tracker.Decay("nope", time.Now.UtcDateTime, recoveryPerMinute: 0.5);

        await Assert.That(dropped).IsFalse();
    }

    [Test]
    public async Task Hold_AdvancesClockWithoutAccruing()
    {
        (TorrentPenaltyTracker tracker, _, TestTimeProvider time) = Create();
        Accrue(tracker, time, "h1");

        time.AdvanceMinutes(10);
        tracker.Hold("h1", time.Now.UtcDateTime);
        // Accruing right after a hold adds nothing because the clock already moved.
        PenaltyResult result = Accrue(tracker, time, "h1");

        await Assert.That(result.PenaltyMinutes).IsEqualTo(0);
    }

    [Test]
    public async Task TryClaimDeletion_RemovesAndReturnsTrueOnce()
    {
        (TorrentPenaltyTracker tracker, _, TestTimeProvider time) = Create();
        Accrue(tracker, time, "h1");

        await Assert.That(tracker.TryClaimDeletion("h1")).IsTrue();
        await Assert.That(tracker.TryClaimDeletion("h1")).IsFalse();
        await Assert.That(tracker.GetPenalties()).IsEmpty();
    }

    [Test]
    public async Task Clear_ReturnsWhetherTorrentWasTracked()
    {
        (TorrentPenaltyTracker tracker, _, TestTimeProvider time) = Create();

        await Assert.That(tracker.Clear("h1")).IsFalse();
        Accrue(tracker, time, "h1");
        await Assert.That(tracker.Clear("h1")).IsTrue();
        await Assert.That(tracker.GetPenalties()).IsEmpty();
    }

    [Test]
    public async Task Prune_DropsTorrentsNoLongerActive()
    {
        (TorrentPenaltyTracker tracker, _, TestTimeProvider time) = Create();
        Accrue(tracker, time, "h1");
        Accrue(tracker, time, "h2");

        tracker.Prune(["h1"]);

        IReadOnlyList<TorrentPenalty> penalties = tracker.GetPenalties();
        await Assert.That(penalties.Count).IsEqualTo(1);
        await Assert.That(penalties[0].Hash).IsEqualTo("h1");
    }

    [Test]
    public async Task GetPenalties_OrdersByDistanceToLimit()
    {
        (TorrentPenaltyTracker tracker, _, TestTimeProvider time) = Create();
        Accrue(tracker, time, "close", limitMinutes: 10);
        time.AdvanceMinutes(8);
        Accrue(tracker, time, "close", limitMinutes: 10);

        Accrue(tracker, time, "far", limitMinutes: 100);
        time.AdvanceMinutes(2);
        Accrue(tracker, time, "far", limitMinutes: 100);

        IReadOnlyList<TorrentPenalty> penalties = tracker.GetPenalties();

        await Assert.That(penalties[0].Hash).IsEqualTo("close");
        await Assert.That(penalties[0].Fraction).IsGreaterThan(penalties[1].Fraction);
    }

    [Test]
    public async Task Revoke_ResetsAndRecordsAuditEvent()
    {
        (TorrentPenaltyTracker tracker, FakeEventStore store, TestTimeProvider time) = Create();
        Accrue(tracker, time, "h1");

        bool revoked = await tracker.Revoke("h1", "alice");

        await Assert.That(revoked).IsTrue();
        await Assert.That(tracker.GetPenalties()).IsEmpty();
        await Assert.That(store.Events.Count).IsEqualTo(1);
        await Assert.That(store.Events[0].EventType).IsEqualTo(DownloadEventType.PenaltyRevoked);
        await Assert.That(store.Events[0].Category).IsEqualTo(EventCategory.Download);
        await Assert.That(store.Events[0].Source).IsEqualTo("h1");
        await Assert.That(store.Events[0].Message).Contains("alice");
    }

    [Test]
    public async Task Revoke_UnknownTorrent_ReturnsFalseAndRecordsNothing()
    {
        (TorrentPenaltyTracker tracker, FakeEventStore store, _) = Create();

        bool revoked = await tracker.Revoke("nope", "alice");

        await Assert.That(revoked).IsFalse();
        await Assert.That(store.Events).IsEmpty();
    }
}
