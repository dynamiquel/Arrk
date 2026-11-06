using Microsoft.Extensions.AI;
using Arrk.Ai.Data;

namespace Arrk.Ai.Tests;

/// <summary>
/// The one-shot assistant turn. These pin the safety rails that matter for an advisory popup:
/// nothing runs while AI is off, limits stop a question before the model is called, a tool result is
/// streamed as an activity update, one question is one usage row, and a provider failure is a plain
/// message instead of an exception.
/// </summary>
public class AssistantServiceTests
{
    static async Task<List<AssistantUpdate>> Collect(IAsyncEnumerable<AssistantUpdate> updates)
    {
        List<AssistantUpdate> collected = [];
        await foreach (AssistantUpdate update in updates)
            collected.Add(update);
        return collected;
    }

    [Test]
    public async Task Disabled_ReportsTheReason_AndNeverCallsTheModel()
    {
        using AssistantTestHost host = new(new AiConfig { Enabled = false });
        ScriptedChatClient client = new();
        AssistantService service = host.CreateService(client);

        List<AssistantUpdate> updates = await Collect(
            service.StreamAsync(AssistantTestHost.User(), "hello"));

        AssistantFailed failed = updates.OfType<AssistantFailed>().Single();
        await Assert.That(failed.Message).Contains("AI is off");
        await Assert.That(client.Requests.Count).IsEqualTo(0);
        await Assert.That(updates.OfType<AssistantTextDelta>().Count()).IsEqualTo(0);
    }

    [Test]
    public async Task PerUserLimit_StopsTheQuestion_BeforeTheModelIsCalled()
    {
        AiConfig config = new()
        {
            Enabled = true,
            Provider = AiProviders.Ollama,
            Models = new AiModelsConfig { Chat = "m" },
            Features = new AiFeaturesConfig
            {
                Assistant = new AiAssistantFeature { MaxMessagesPerHour = 1 }
            }
        };
        using AssistantTestHost host = new(config);
        await host.Usage.RecordAsync(host.Usage.CreateRecord(config, AiFeatureKeys.Assistant, "ask",
            AiModelRole.Chat, 5, 5, 10, success: true, cacheHit: false, userId: 1,
            error: null, promptText: null, responseText: null));

        ScriptedChatClient client = new ScriptedChatClient().ThenAnswer("should not be used");
        AssistantService service = host.CreateService(client);

        List<AssistantUpdate> updates = await Collect(
            service.StreamAsync(AssistantTestHost.User(), "hello"));

        AssistantFailed failed = updates.OfType<AssistantFailed>().Single();
        await Assert.That(failed.Message).Contains("limit");
        await Assert.That(client.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StreamsTheAnswer_AndRecordsOneUsageRowPerQuestion()
    {
        using AssistantTestHost host = new();
        ScriptedChatClient client = new ScriptedChatClient().ThenAnswer("Hello there", inputTokens: 12, outputTokens: 4);
        AssistantService service = host.CreateService(client);

        List<AssistantUpdate> updates = await Collect(
            service.StreamAsync(AssistantTestHost.User(), "Hi, who are you?"));

        await Assert.That(string.Concat(updates.OfType<AssistantTextDelta>().Select(u => u.Text)))
            .IsEqualTo("Hello there");
        AssistantCompleted completed = updates.OfType<AssistantCompleted>().Single();
        await Assert.That(completed.Cancelled).IsFalse();
        await Assert.That(completed.Tools.Count).IsEqualTo(0);

        IReadOnlyList<AiUsageRecord> usage = await host.Usage.GetRecentAsync();
        await Assert.That(usage.Count).IsEqualTo(1);
        await Assert.That(usage[0].Feature).IsEqualTo(AiFeatureKeys.Assistant);
        await Assert.That(usage[0].Operation).IsEqualTo("ask");
        await Assert.That(usage[0].UserId).IsEqualTo(1);
        await Assert.That(usage[0].PromptTokens).IsEqualTo(12);
        await Assert.That(usage[0].CompletionTokens).IsEqualTo(4);
        await Assert.That(usage[0].Success).IsTrue();
    }

    [Test]
    public async Task RunsATool_AndStreamsItsActivity()
    {
        EchoAssistantTool pack = new();
        using AssistantTestHost host = new(packs: [pack]);
        ScriptedChatClient client = new ScriptedChatClient()
            .ThenToolCall("echo_lookup", new Dictionary<string, object?> { ["query"] = "dune" })
            .ThenAnswer("Found it");
        AssistantService service = host.CreateService(client);

        List<AssistantUpdate> updates = await Collect(
            service.StreamAsync(AssistantTestHost.User(), "look up dune"));

        AssistantToolStarted started = updates.OfType<AssistantToolStarted>().Single();
        await Assert.That(started.Name).IsEqualTo("echo_lookup");
        await Assert.That(started.Label).IsEqualTo("Echo");

        AssistantToolFinished finished = updates.OfType<AssistantToolFinished>().Single();
        await Assert.That(finished.Success).IsTrue();
        await Assert.That(finished.Summary).Contains("found:dune");

        await Assert.That(string.Concat(updates.OfType<AssistantTextDelta>().Select(u => u.Text)))
            .IsEqualTo("Found it");
        await Assert.That(client.Requests.Count).IsEqualTo(2);
        await Assert.That(client.Requests[0].Options!.Tools!.Select(t => t.Name).ToList())
            .Contains("echo_lookup");

        AssistantCompleted completed = updates.OfType<AssistantCompleted>().Single();
        await Assert.That(completed.Tools.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ARepeatedToolCall_IsExecutedOnce()
    {
        EchoAssistantTool pack = new();
        using AssistantTestHost host = new(packs: [pack]);
        ScriptedChatClient client = new ScriptedChatClient()
            .ThenToolCall("echo_lookup", new Dictionary<string, object?> { ["query"] = "dune" }, "call-1")
            .ThenToolCall("echo_lookup", new Dictionary<string, object?> { ["query"] = "dune" }, "call-2")
            .ThenAnswer("done");
        AssistantService service = host.CreateService(client);

        List<AssistantUpdate> updates = await Collect(
            service.StreamAsync(AssistantTestHost.User(), "look up dune twice"));

        await Assert.That(pack.Calls).IsEqualTo(1);
        await Assert.That(updates.OfType<AssistantToolStarted>().Count()).IsEqualTo(1);
        await Assert.That(updates.OfType<AssistantToolFinished>().Count()).IsEqualTo(1);
        await Assert.That(updates.OfType<AssistantCompleted>().Single().Tools.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EveryQuestion_IsIndependent_WithNoHistoryReplayed()
    {
        using AssistantTestHost host = new();
        ScriptedChatClient client = new ScriptedChatClient()
            .ThenAnswer("first")
            .ThenAnswer("second");
        AssistantService service = host.CreateService(client);

        await Collect(service.StreamAsync(AssistantTestHost.User(), "first question"));
        await Collect(service.StreamAsync(AssistantTestHost.User(), "second question"));

        await Assert.That(client.Requests.Count).IsEqualTo(2);
        // system + the current question only, the earlier answer is never sent again.
        await Assert.That(client.Requests[1].Messages.Count).IsEqualTo(2);
        await Assert.That(client.Requests[1].Messages[^1].Text).IsEqualTo("second question");
    }

    [Test]
    public async Task SystemPrompt_CarriesTheCallerAndTheFactsPolicy()
    {
        using AssistantTestHost host = new();
        ScriptedChatClient client = new ScriptedChatClient().ThenAnswer("ok");
        AssistantService service = host.CreateService(client);

        await Collect(service.StreamAsync(AssistantTestHost.User(7, "bob", admin: true), "hello"));

        ChatMessage system = client.Requests[0].Messages[0];
        await Assert.That(system.Role).IsEqualTo(ChatRole.System);
        await Assert.That(system.Text).Contains("bob");
        await Assert.That(system.Text).Contains("admin");
        await Assert.That(system.Text).Contains("facts must come from a tool");
        await Assert.That(system.Text).Contains("plain text");
        await Assert.That(client.Requests[0].Messages[^1].Text).IsEqualTo("hello");
    }

    [Test]
    public async Task ProviderFailure_IsReported_AndRecordedAsAFailedCall()
    {
        using AssistantTestHost host = new();
        ScriptedChatClient client = new ScriptedChatClient().ThenThrow(new InvalidOperationException("provider down"));
        AssistantService service = host.CreateService(client);

        List<AssistantUpdate> updates = await Collect(
            service.StreamAsync(AssistantTestHost.User(), "hello"));

        AssistantFailed failed = updates.OfType<AssistantFailed>().Single();
        await Assert.That(failed.Message).Contains("InvalidOperationException");

        IReadOnlyList<AiUsageRecord> usage = await host.Usage.GetRecentAsync();
        await Assert.That(usage.Count).IsEqualTo(1);
        await Assert.That(usage[0].Success).IsFalse();
        await Assert.That(usage[0].Error).Contains("InvalidOperationException");
    }

    [Test]
    public async Task ToolChips_UseFriendlyLabels()
    {
        await Assert.That(AssistantService.LabelForTool("search_media", "Library")).IsEqualTo("Searched library");
        await Assert.That(AssistantService.LabelForTool("get_continue_watching", "Library")).IsEqualTo("Checked watch progress");
        await Assert.That(AssistantService.LabelForTool("custom_tool", "Echo")).IsEqualTo("Echo");
        await Assert.That(AssistantService.LabelForTool("tv_play_media", "Movies & TV actions")).IsEqualTo("Started playback");
        await Assert.That(AssistantService.LabelForTool("music_recommend_media", "Music recommendations")).IsEqualTo("Picked suggestions");
    }

    [Test]
    public async Task FollowUpTurn_ReplaysTheThread_AndInjectsRecentItems()
    {
        using AssistantTestHost host = new(packs: [new SearchTestTool()]);
        ScriptedChatClient firstClient = new ScriptedChatClient()
            .ThenToolCall("search_test", new Dictionary<string, object?> { ["query"] = "spiderman" })
            .ThenAnswer("Found Spider-Man: Into the Spider-Verse and Spider-Man 2.");

        List<AssistantUpdate> first = await Collect(host
            .CreateService(firstClient)
            .StreamAsync(AssistantTestHost.User(), "whats the latest spiderman movie"));
        AssistantConversation conversation = first.OfType<AssistantCompleted>().Single().Conversation!;

        await Assert.That(conversation.Turns.Count).IsEqualTo(2);
        await Assert.That(conversation.RecentItems.Count).IsEqualTo(2);

        ScriptedChatClient secondClient = new ScriptedChatClient().ThenAnswer("Requesting the first one.");
        await Collect(host.CreateService(secondClient).StreamAsync(
            AssistantTestHost.User(), "download the first one", conversation));

        IReadOnlyList<ChatMessage> messages = secondClient.Requests[0].Messages;
        await Assert.That(messages[0].Role).IsEqualTo(ChatRole.System);
        await Assert.That(messages[0].Text).Contains("tv:movie:1");
        await Assert.That(messages[0].Text).Contains("Spider-Man: Into the Spider-Verse");
        await Assert.That(messages.Any(m => m.Role == ChatRole.User
            && m.Text == "whats the latest spiderman movie")).IsTrue();
        await Assert.That(messages.Any(m => m.Role == ChatRole.Assistant
            && m.Text!.Contains("Found Spider-Man"))).IsTrue();
        await Assert.That(messages[^1].Text).IsEqualTo("download the first one");
    }

    [Test]
    public async Task PersonaChoice_IsReplayedIntoTheSystemPrompt()
    {
        using AssistantTestHost host = new();
        FakePersonaService personas = new("jarvis");
        ScriptedChatClient client = new ScriptedChatClient().ThenAnswer("At your service.");

        await Collect(host.CreateService(client, personas: personas)
            .StreamAsync(AssistantTestHost.User(), "hello"));

        await Assert.That(client.Requests[0].Messages[0].Text).Contains("Persona: Jarvis");
        await Assert.That(personas.Key).IsEqualTo("jarvis");
    }

    [Test]
    public async Task PersonaSelection_PersistsThroughTheService()
    {
        using AssistantTestHost host = new();
        FakePersonaService personas = new("plain");
        AssistantService service = host.CreateService(new ScriptedChatClient(), personas: personas);

        AssistantPersona? saved = await service.SetPersonaAsync(AssistantTestHost.User(), "cortana");
        AssistantPersona selected = await service.GetPersonaAsync(AssistantTestHost.User());

        await Assert.That(saved).IsNotNull();
        await Assert.That(personas.Key).IsEqualTo("cortana");
        await Assert.That(selected.Key).IsEqualTo("cortana");
    }

    [Test]
    public async Task ConversationAppend_IsBounded()
    {
        AssistantConversation conversation = AssistantConversation.Empty;
        for (int i = 0; i < 15; i++)
        {
            conversation = AssistantConversation.Append(conversation, $"q{i}", $"a{i}",
                [new AssistantMediaSummary("tv", $"tv:movie:{i}", AssistantMediaKind.Movie, $"Film {i}")]);
        }

        await Assert.That(conversation.Turns.Count).IsEqualTo(AssistantConversation.MaxTurns);
        await Assert.That(conversation.Turns[^1].Text).IsEqualTo("a14");
        await Assert.That(conversation.RecentItems.Count).IsEqualTo(AssistantConversation.MaxRecentItems);
        await Assert.That(conversation.RecentItems[^1].Id).IsEqualTo("tv:movie:14");
    }

    [Test]
    public async Task ToolUiActions_AreStreamedOnce_ForTheOverlay()
    {
        using AssistantTestHost host = new(packs: [new PlaybackTool()]);
        ScriptedChatClient client = new ScriptedChatClient()
            .ThenToolCall("play_test", new Dictionary<string, object?> { ["id"] = "tv:movie:1" })
            .ThenAnswer("Ready to play.");

        List<AssistantUpdate> updates = await Collect(
            host.CreateService(client).StreamAsync(AssistantTestHost.User(), "play dune"));

        List<AssistantActionAvailable> actions = updates.OfType<AssistantActionAvailable>().ToList();
        await Assert.That(actions.Count).IsEqualTo(1);
        await Assert.That(actions[0].Link.Url).IsEqualTo("/watch/1");
        await Assert.That(updates.OfType<AssistantToolFinished>().Single().Success).IsTrue();
    }

    sealed class FakePersonaService(string? key) : AiPersonaService(null!, TimeProvider.System)
    {
        public string? Key { get; private set; } = key;

        public override Task<string?> GetKeyAsync(int userId, CancellationToken ct = default) =>
            Task.FromResult(Key);

        public override Task SetKeyAsync(int userId, string personaKey, CancellationToken ct = default)
        {
            Key = personaKey;
            return Task.CompletedTask;
        }
    }

    sealed class SearchTestTool : IAssistantTool
    {
        public string Key => "search-test";
        public string Name => "Search test";
        public string Description => "Returns two test media items.";
        public AssistantAccess Access => AssistantAccess.User;
        public IReadOnlyList<string> Keywords => [];
        public bool IsAvailable => true;

        public IReadOnlyList<AITool> CreateTools(AssistantToolContext context) =>
        [
            AIFunctionFactory.Create((string query) =>
            {
                context.AddContextItems(
                [
                    new AssistantMediaSummary("tv", "tv:movie:1", AssistantMediaKind.Movie,
                        "Spider-Man: Into the Spider-Verse", "Movie", 2018, "Not in library"),
                    new AssistantMediaSummary("tv", "tv:movie:2", AssistantMediaKind.Movie,
                        "Spider-Man 2", "Movie", 2004, "In library")
                ]);
                return $"searched:{query}";
            }, "search_test", "Looks up test media.")
        ];
    }

    sealed class PlaybackTool : IAssistantTool
    {
        public string Key => "play";
        public string Name => "Play";
        public string Description => "A test playback action.";
        public AssistantAccess Access => AssistantAccess.User;
        public IReadOnlyList<string> Keywords => [];
        public bool IsAvailable => true;

        public IReadOnlyList<AITool> CreateTools(AssistantToolContext context) =>
        [
            AIFunctionFactory.Create((string id) =>
            {
                context.AddUiAction(new AssistantActionLink(
                    AssistantActionLinkKind.Open, "Play", "/watch/1"));
                return "ok";
            }, "play_test", "Starts a test playback.")
        ];
    }

    [Test]
    public async Task Describe_ExplainsAProviderStatus_InsteadOfLeakingTheException()
    {
        string generic = AssistantService.Describe(new InvalidOperationException("boom"));

        await Assert.That(generic).Contains("InvalidOperationException");
        await Assert.That(generic).Contains("Configuration → AI");
    }
}
