using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Arrk.Ai.Tests;

/// <summary>
/// The shared action runner. It is the single place that enforces admin-only deletion/upgrades,
/// routes an id to the module that owns it, and turns a "please confirm" outcome into the overlay's
/// one-click confirmation button.
/// </summary>
public class AssistantActionRunnerTests
{
    static AssistantToolContext Context(bool admin = false, IList<AssistantActionLink>? links = null) => new(
        1, "alice", admin, new ServiceCollection().BuildServiceProvider(), CancellationToken.None, links);

    [Test]
    public async Task NonAdminDelete_IsBlockedBeforeTheSourceIsCalled()
    {
        FakeSource source = new("tv");
        AssistantActionRunner runner = new([source]);

        AssistantActionOutcome outcome = await runner.ExecuteAsync(
            AssistantActionKind.Delete, "tv:movie:1", null, confirmed: true, Context(), default);

        await Assert.That(outcome.Success).IsFalse();
        await Assert.That(outcome.Message).Contains("administrator");
        await Assert.That(source.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task ConfirmationOutcome_AddsAConfirmButtonWithThePendingAction()
    {
        FakeSource source = new("tv") { RequireConfirmation = true };
        List<AssistantActionLink> links = [];
        AssistantActionRunner runner = new([source]);

        AssistantActionOutcome outcome = await runner.ExecuteAsync(
            AssistantActionKind.Delete, "tv:movie:1", null, confirmed: false, Context(admin: true, links), default);

        await Assert.That(outcome.RequiresConfirmation).IsTrue();
        AssistantActionLink button = links.Single(l => l.Kind == AssistantActionLinkKind.Confirm);
        AssistantPendingAction? pending = JsonSerializer.Deserialize<AssistantPendingAction>(button.Payload!);
        await Assert.That(pending!.Action).IsEqualTo(AssistantActionKind.Delete);
        await Assert.That(pending.Id).IsEqualTo("tv:movie:1");
        await Assert.That(pending.Title).Contains("Delete");
    }

    [Test]
    public async Task Upgrade_WithoutConfirmation_DoesNotReachTheSource()
    {
        FakeSource source = new("tv");
        List<AssistantActionLink> links = [];
        AssistantActionRunner runner = new([source]);

        AssistantActionOutcome outcome = await runner.ExecuteAsync(
            AssistantActionKind.Upgrade, "tv:movie:1", "UHD", confirmed: false,
            Context(admin: true, links), default);

        await Assert.That(outcome.RequiresConfirmation).IsTrue();
        await Assert.That(source.Calls).IsEqualTo(0);
        await Assert.That(links.Single(l => l.Kind == AssistantActionLinkKind.Confirm)).IsNotNull();
    }

    [Test]
    public async Task Upgrade_AfterConfirmation_ReachesTheSource()
    {
        FakeSource source = new("tv");
        AssistantActionRunner runner = new([source]);

        AssistantActionOutcome outcome = await runner.ExecuteAsync(
            AssistantActionKind.Upgrade, "tv:movie:1", "UHD", confirmed: true,
            Context(admin: true), default);

        await Assert.That(outcome.Success).IsTrue();
        await Assert.That(source.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task UnknownPrefix_FailsWithoutCallingAnySource()
    {
        FakeSource source = new("tv");
        AssistantActionRunner runner = new([source]);

        AssistantActionOutcome outcome = await runner.ExecuteAsync(
            AssistantActionKind.Request, "music:album:dz:1", null, false, Context(), default);

        await Assert.That(outcome.Success).IsFalse();
        await Assert.That(source.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task PlayLink_IsQueuedForTheOverlay()
    {
        FakeSource source = new("tv")
        {
            Link = new AssistantActionLink(AssistantActionLinkKind.Open, "Play", "/watch/9")
        };
        List<AssistantActionLink> links = [];
        AssistantActionRunner runner = new([source]);

        await runner.ExecuteAsync(
            AssistantActionKind.Play, "tv:movie:1", null, false, Context(links: links), default);

        await Assert.That(links.Count).IsEqualTo(1);
        await Assert.That(links[0].Url).IsEqualTo("/watch/9");
    }

    sealed class FakeSource(string key) : IAssistantMediaActionSource
    {
        public string Key { get; } = key;
        public bool IsAvailable => true;
        public bool RequireConfirmation { get; set; }
        public AssistantActionLink? Link { get; set; }
        public int Calls { get; private set; }

        public IReadOnlyList<AssistantActionKind> SupportedActions =>
        [
            AssistantActionKind.Request,
            AssistantActionKind.Delete,
            AssistantActionKind.Upgrade,
            AssistantActionKind.Play
        ];

        public Task<AssistantActionOutcome> ExecuteAsync(
            AssistantActionKind action,
            string id,
            string? option,
            bool confirmed,
            AssistantToolContext context,
            CancellationToken ct)
        {
            Calls++;
            if (RequireConfirmation && !confirmed)
                return Task.FromResult(AssistantActionOutcome.Confirm("Confirm?", "Delete Test"));
            return Task.FromResult(AssistantActionOutcome.Ok("done", Link));
        }
    }
}
