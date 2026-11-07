using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Auth.Abstractions;
using Arrk.Core.Enums;
using Arrk.Notifications;
using Arrk.Core.Requests;
using Arrk.Requests.Models;
using Arrk.TestSupport;

namespace Arrk.Requests.Tests;

public class RequestServiceTests
{
    static RequestTarget Movie(int tmdb = 100)
    {
        Dictionary<string, string> ids = new() { ["tmdb"] = tmdb.ToString() };
        return new RequestTarget(new MediaDomain("movie"), RequestKind.Movie, ids, "Test Movie", 2020, PosterUrl: "/p.jpg");
    }

    static RequestTarget Show(int tvdb = 500) => new(
        new MediaDomain("tv"), RequestKind.Series,
        new Dictionary<string, string> { ["tvdb"] = tvdb.ToString(), ["tmdb"] = "77" },
        "Test Show", 2019);

    static (RequestService Service, StubRequestHandler Handler, RecordingNotificationService Notifications, TestDbContextFactory Db, FakeEventStore Events)
        Build(RequestsConfig? cfg = null)
    {
        TestDbContextFactory db = TestDb.Factory(out _);
        StubRequestHandler handler = new("tv");
        RecordingNotificationService notifications = new();
        FakeEventStore events = new();
        NotificationSubscriptionService subscriptions = new(
            db, TimeProvider.System, NullLogger<NotificationSubscriptionService>.Instance);
        RequestService service = new(
            db,
            new MediaRequestRegistry([handler]),
            notifications,
            subscriptions,
            events,
            null!,
            new MutableOptionsMonitor<RequestsConfig>(cfg ?? new RequestsConfig()),
            TimeProvider.System,
            NullLogger<RequestService>.Instance);

        return (service, handler, notifications, db, events);
    }

    [Test]
    public async Task Create_persists_pending_request_and_notifies()
    {
        (RequestService service, StubRequestHandler handler, RecordingNotificationService notifications, _, FakeEventStore events) = Build();

        RequestOperationResult result = await service.CreateAsync(7, "alice", Movie());

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Request!.Status).IsEqualTo(RequestStatus.Pending);
        await Assert.That(result.Request.RequestedByUsername).IsEqualTo("alice");
        await Assert.That(result.Request.PosterUrl).IsEqualTo("/p.jpg");

        await Assert.That(notifications.Published.Count).IsEqualTo(1);
        NotificationAudience audience = notifications.Published[0].Message.Audience;
        await Assert.That(audience.Roles.SequenceEqual([ArrkRoles.Admin])).IsTrue();
        await Assert.That(audience.UserIds).IsEmpty();
        await Assert.That(audience.FollowersOf).IsNull();
        await Assert.That(events.Records.Count).IsEqualTo(1);
        await Assert.That(handler.Fulfilled.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Duplicate_request_is_rejected()
    {
        (RequestService service, _, _, _, _) = Build();

        await service.CreateAsync(7, "alice", Movie());
        RequestOperationResult second = await service.CreateAsync(7, "alice", Movie());

        await Assert.That(second.Success).IsFalse();
    }

    [Test]
    public async Task Already_in_library_is_rejected_with_state()
    {
        (RequestService service, StubRequestHandler handler, _, _, _) = Build();
        handler.CheckResult = RequestTargetState.InLibrary("9", hasFile: true);

        RequestOperationResult result = await service.CreateAsync(7, "alice", Movie());

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.TargetState).IsNotNull();
    }

    [Test]
    public async Task Approve_fulfils_via_handler_and_notifies_requester()
    {
        (RequestService service, StubRequestHandler handler, RecordingNotificationService notifications, _, _) = Build();
        RequestOperationResult created = await service.CreateAsync(7, "alice", Movie());

        RequestOperationResult approved = await service.ApproveAsync(created.Request!.Id, adminUserId: 1);

        await Assert.That(approved.Success).IsTrue();
        await Assert.That(approved.Request!.Status).IsEqualTo(RequestStatus.Approved);
        await Assert.That(approved.Request.LibraryId).IsEqualTo("42");
        await Assert.That(handler.Fulfilled.Count).IsEqualTo(1);

        (NotificationMessage Message, int? UserId) last = notifications.Published[^1];
        await Assert.That(last.UserId).IsEqualTo(7);
        await Assert.That(last.Message.Key).IsEqualTo(RequestsNotificationTopics.Approved);
    }

    [Test]
    public async Task Deny_sets_status_and_notifies_requester()
    {
        (RequestService service, _, RecordingNotificationService notifications, _, _) = Build();
        RequestOperationResult created = await service.CreateAsync(7, "alice", Movie());

        RequestOperationResult denied = await service.DenyAsync(created.Request!.Id, adminUserId: 1, reason: "nope");

        await Assert.That(denied.Request!.Status).IsEqualTo(RequestStatus.Denied);
        await Assert.That(denied.Request.DecisionReason).IsEqualTo("nope");
        await Assert.That(notifications.Published[^1].UserId).IsEqualTo(7);
        await Assert.That(notifications.Published[^1].Message.Key).IsEqualTo(RequestsNotificationTopics.Denied);
    }

    [Test]
    public async Task Blacklist_blocks_future_requests()
    {
        (RequestService service, _, _, _, _) = Build();
        RequestOperationResult created = await service.CreateAsync(7, "alice", Movie());

        await service.BlacklistAsync(created.Request!.Id, adminUserId: 1, reason: "spam");
        RequestOperationResult again = await service.CreateAsync(8, "bob", Movie());

        await Assert.That(again.Success).IsFalse();
        await Assert.That(again.Message).Contains("blacklisted");
    }

    [Test]
    public async Task Auto_approve_user_skips_review()
    {
        RequestsConfig cfg = new() { AutoApproveUsers = ["alice"] };
        (RequestService service, StubRequestHandler handler, _, _, _) = Build(cfg);

        RequestOperationResult result = await service.CreateAsync(7, "alice", Movie());

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Request!.Status).IsEqualTo(RequestStatus.Approved);
        await Assert.That(handler.Fulfilled.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Disabled_system_rejects_requests()
    {
        (RequestService service, _, _, _, _) = Build(new RequestsConfig { Enabled = false });

        RequestOperationResult result = await service.CreateAsync(7, "alice", Movie());

        await Assert.That(result.Success).IsFalse();
    }

    [Test]
    public async Task Disallowed_kind_is_rejected()
    {
        (RequestService service, _, _, _, _) = Build(new RequestsConfig { AllowSeriesRequests = false });

        RequestOperationResult result = await service.CreateAsync(7, "alice", Show());

        await Assert.That(result.Success).IsFalse();
    }

    [Test]
    public async Task Summary_counts_statuses()
    {
        (RequestService service, _, _, _, _) = Build();
        RequestOperationResult a = await service.CreateAsync(1, "a", Movie(1));
        RequestOperationResult b = await service.CreateAsync(1, "a", Movie(2));
        await service.ApproveAsync(a.Request!.Id, 99);
        await service.DenyAsync(b.Request!.Id, 99, "no");

        RequestsSummary summary = await service.GetSummaryAsync();

        await Assert.That(summary.Open).IsEqualTo(0);
        await Assert.That(summary.Accepted).IsEqualTo(1);
        await Assert.That(summary.Denied).IsEqualTo(1);
        await Assert.That(summary.Total).IsEqualTo(2);
    }

    [Test]
    public async Task Unavailable_handler_is_rejected()
    {
        (RequestService service, StubRequestHandler handler, _, _, _) = Build();
        handler.IsAvailable = false;

        RequestOperationResult result = await service.CreateAsync(7, "alice", Movie());

        await Assert.That(result.Success).IsFalse();
    }

    [Test]
    public async Task Max_open_per_user_is_enforced()
    {
        (RequestService service, _, _, _, _) = Build(new RequestsConfig { MaxOpenPerUser = 1 });
        await service.CreateAsync(7, "alice", Movie(1));

        RequestOperationResult second = await service.CreateAsync(7, "alice", Movie(2));

        await Assert.That(second.Success).IsFalse();
    }

    static MediaEntityRef MovieEntity(int tmdb = 100) => new(
        "tv", new MediaDomain("movie"), $"tv:movie:tmdb:{tmdb}", "Test Movie", Url: "/movies");

    static MediaEntityRef SeriesEntity(int tvdb = 500) => new(
        "tv", new MediaDomain("tv"), $"tv:series:tvdb:{tvdb}", "Test Show", Url: "/shows");

    static MediaEntityRef EpisodeEntity(int tvdb = 500, int season = 1, int episode = 2) => new(
        "tv",
        new MediaDomain("tv"),
        $"tv:series:tvdb:{tvdb}:s{season}e{episode}",
        "Test Show",
        AncestorItemKeys: [$"tv:series:tvdb:{tvdb}:s{season}", $"tv:series:tvdb:{tvdb}"],
        Url: "/shows");

    [Test]
    public async Task Create_with_resolvable_entity_subscribes_the_requester()
    {
        (RequestService service, StubRequestHandler handler, _, TestDbContextFactory db, _) = Build();
        handler.Entity = MovieEntity();

        await service.CreateAsync(7, "alice", Movie());

        NotificationSubscriptionService subscriptions = new(
            db, TimeProvider.System, NullLogger<NotificationSubscriptionService>.Instance);
        IReadOnlyList<NotificationSubscription> following = await subscriptions.GetForUserAsync(7);

        await Assert.That(following.Count).IsEqualTo(1);
        await Assert.That(following[0].ItemKey).IsEqualTo("tv:movie:tmdb:100");
        await Assert.That(following[0].Source).IsEqualTo("request");
        await Assert.That(following[0].IncludeDescendants).IsFalse();
    }

    [Test]
    public async Task Status_toggle_off_suppresses_request_decision_notifications()
    {
        (RequestService service, StubRequestHandler handler, RecordingNotificationService notifications, TestDbContextFactory db, _) = Build();
        handler.Entity = MovieEntity();
        RequestOperationResult created = await service.CreateAsync(7, "alice", Movie());

        NotificationSubscriptionService subscriptions = new(
            db, TimeProvider.System, NullLogger<NotificationSubscriptionService>.Instance);
        NotificationSubscription subscription = (await subscriptions.GetForUserAsync(7)).Single();
        await subscriptions.UpdateOptionsAsync(subscription.Id, 7, new NotificationSubscriptionOptions
        {
            Status = false
        });

        await service.ApproveAsync(created.Request!.Id, adminUserId: 1);

        await Assert.That(notifications.Published.Any(
            p => p.Message.Key == RequestsNotificationTopics.Approved)).IsFalse();
    }

    [Test]
    public async Task Deny_removes_the_request_subscription()
    {
        (RequestService service, StubRequestHandler handler, _, TestDbContextFactory db, _) = Build();
        handler.Entity = MovieEntity();
        RequestOperationResult created = await service.CreateAsync(7, "alice", Movie());

        await service.DenyAsync(created.Request!.Id, adminUserId: 1, reason: "nope");

        NotificationSubscriptionService subscriptions = new(
            db, TimeProvider.System, NullLogger<NotificationSubscriptionService>.Instance);
        await Assert.That(await subscriptions.GetForUserAsync(7)).IsEmpty();
    }

    [Test]
    public async Task Import_marks_a_matching_movie_request_available_once()
    {
        (RequestService service, StubRequestHandler handler, RecordingNotificationService notifications, _, _) = Build();
        handler.Entity = MovieEntity();
        RequestOperationResult created = await service.CreateAsync(7, "alice", Movie());
        await service.ApproveAsync(created.Request!.Id, adminUserId: 1);

        int matched = await service.MarkAvailableAsync(MovieEntity());
        int again = await service.MarkAvailableAsync(MovieEntity());

        await Assert.That(matched).IsEqualTo(1);
        await Assert.That(again).IsEqualTo(0);
        MediaRequest? reloaded = await service.GetByIdAsync(created.Request.Id);
        await Assert.That(reloaded!.Status).IsEqualTo(RequestStatus.Available);
        await Assert.That(notifications.Published[^1].Message.Key).IsEqualTo(RequestsNotificationTopics.Available);
        await Assert.That(notifications.Published[^1].UserId).IsEqualTo(7);
    }

    [Test]
    public async Task Import_of_an_episode_marks_a_series_request_available()
    {
        (RequestService service, StubRequestHandler handler, _, _, _) = Build();
        handler.Entity = SeriesEntity();
        RequestOperationResult created = await service.CreateAsync(7, "alice", Show());
        await service.ApproveAsync(created.Request!.Id, adminUserId: 1);

        int matched = await service.MarkAvailableAsync(EpisodeEntity());

        await Assert.That(matched).IsEqualTo(1);
        MediaRequest? reloaded = await service.GetByIdAsync(created.Request.Id);
        await Assert.That(reloaded!.Status).IsEqualTo(RequestStatus.Available);
    }

    [Test]
    public async Task Removing_media_cancels_an_approved_request()
    {
        (RequestService service, _, _, _, _) = Build();
        RequestOperationResult created = await service.CreateAsync(7, "alice", Movie(100));
        await service.ApproveAsync(created.Request!.Id, adminUserId: 1);

        int changed = await service.CancelForMediaAsync(new MediaDomain("movie"), new Dictionary<string, string> { ["tmdb"] = "100" });

        await Assert.That(changed).IsEqualTo(1);
        MediaRequest? reloaded = await service.GetByIdAsync(created.Request.Id);
        await Assert.That(reloaded!.Status).IsEqualTo(RequestStatus.Cancelled);
    }

    [Test]
    public async Task Removing_media_also_removes_the_request_subscription()
    {
        (RequestService service, StubRequestHandler handler, _, TestDbContextFactory db, _) = Build();
        handler.Entity = MovieEntity();
        RequestOperationResult created = await service.CreateAsync(7, "alice", Movie());
        await service.ApproveAsync(created.Request!.Id, adminUserId: 1);

        await service.CancelForMediaAsync(
            new MediaDomain("movie"),
            new Dictionary<string, string> { ["tmdb"] = "100" });

        NotificationSubscriptionService subscriptions = new(
            db, TimeProvider.System, NullLogger<NotificationSubscriptionService>.Instance);
        await Assert.That(await subscriptions.GetForUserAsync(7)).IsEmpty();
    }

    [Test]
    public async Task Removing_unrelated_media_leaves_requests_alone()
    {
        (RequestService service, _, _, _, _) = Build();
        await service.CreateAsync(7, "alice", Movie(100));

        int changed = await service.CancelForMediaAsync(new MediaDomain("movie"), new Dictionary<string, string> { ["tmdb"] = "999" });

        await Assert.That(changed).IsEqualTo(0);
    }

    [Test]
    public async Task Owner_can_revoke_pending_request()
    {
        (RequestService service, _, _, _, _) = Build();
        RequestOperationResult created = await service.CreateAsync(7, "alice", Movie());

        RequestOperationResult revoked = await service.RevokeAsync(created.Request!.Id, userId: 7);

        await Assert.That(revoked.Success).IsTrue();
        await Assert.That(revoked.Request!.Status).IsEqualTo(RequestStatus.Cancelled);
    }

    [Test]
    public async Task Cannot_revoke_someone_elses_request()
    {
        (RequestService service, _, _, _, _) = Build();
        RequestOperationResult created = await service.CreateAsync(7, "alice", Movie());

        RequestOperationResult revoked = await service.RevokeAsync(created.Request!.Id, userId: 8);

        await Assert.That(revoked.Success).IsFalse();
    }

    [Test]
    public async Task Closed_summary_after_revoke_is_not_open()
    {
        (RequestService service, _, _, _, _) = Build();
        RequestOperationResult created = await service.CreateAsync(7, "alice", Movie());
        await service.RevokeAsync(created.Request!.Id, 7);

        RequestsSummary summary = await service.GetSummaryAsync();

        await Assert.That(summary.Open).IsEqualTo(0);
    }
}
