using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.TestSupport;

namespace Stackarr.Ai.Tests;

/// <summary>Builds a real assistant runtime over a real SQLite database and a scripted model.</summary>
public sealed class AssistantTestHost : IDisposable
{
    readonly ServiceProvider _services;

    public AssistantTestHost(AiConfig? config = null, IEnumerable<IAssistantTool>? packs = null,
        IEnumerable<IAssistantMediaSource>? sources = null)
    {
        Config = config ?? new AiConfig
        {
            Enabled = true,
            Provider = AiProviders.Ollama,
            Models = new AiModelsConfig { Chat = "test-model" }
        };

        Monitor = new MutableOptionsMonitor<AiConfig>(Config);
        Time = new TestTimeProvider(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));

        ServiceCollection services = new();
        services.AddLogging();
        _services = services.BuildServiceProvider();

        AiClientFactory factory = new(NullLogger<AiClientFactory>.Instance);
        Ai = new AiService(factory, Monitor, NullLogger<AiService>.Instance, Usage, Budget);
        Registry = new AssistantRegistry(packs ?? [], sources ?? []);
    }

    public TestAiDb Db { get; } = new();
    public AiConfig Config { get; }
    public MutableOptionsMonitor<AiConfig> Monitor { get; }
    public TestTimeProvider Time { get; }
    public AiService Ai { get; }
    public AssistantRegistry Registry { get; }

    AiUsageService? _usage;
    AiBudget? _budget;
    AssistantRateLimiter? _limiter;

    public AiUsageService Usage => _usage ??= new AiUsageService(Db, Time);
    public AiBudget Budget => _budget ??= new AiBudget(Usage, Time);
    public AssistantRateLimiter Limiter => _limiter ??= new AssistantRateLimiter(Usage, Time);

    public AssistantService CreateService(
        IChatClient client, AssistantActionRunner? actions = null, AiPersonaService? personas = null) => new(
        Ai, Registry, Limiter, Usage, Budget,
        _services.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<AssistantService>.Instance,
        Time,
        client,
        actions,
        personas);

    /// <summary>The real service, using the configured provider (for live smoke tests).</summary>
    public AssistantService CreateService() => new(
        Ai, Registry, Limiter, Usage, Budget,
        _services.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<AssistantService>.Instance,
        Time);

    public static AssistantUser User(int id = 1, string name = "alice", bool admin = false) =>
        new(id, name, admin);

    public void Dispose()
    {
        _services.Dispose();
        Db.Dispose();
    }
}

/// <summary>A pack with one function, used to prove the tool loop and its UI events.</summary>
public sealed class EchoAssistantTool : IAssistantTool
{
    public string Key => "echo";
    public string Name => "Echo";
    public string Description => "Echoes a query.";
    public AssistantAccess Access => AssistantAccess.User;
    public IReadOnlyList<string> Keywords => [];
    public bool IsAvailable { get; set; } = true;
    public int Calls { get; private set; }

    public IReadOnlyList<AITool> CreateTools(AssistantToolContext context) =>
    [
        AIFunctionFactory.Create((string query) =>
        {
            Calls++;
            return $"found:{query}";
        }, "echo_lookup", "Looks up a test value.")
    ];
}
