using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Events;
using Arrk.Notifications;
using Arrk.Notifications.Discord;
using Arrk.Notifications.Webhook;
using Arrk.DownloadManagement;

namespace Arrk.Notifications.Tests;

public class NotificationModuleTests
{
    static ServiceProvider Build(IConfiguration? configuration = null, bool clients = true)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContextFactory<ArrkDbContext>(o => o.UseSqlite("Data Source=:memory:"));

        IConfiguration config = configuration ?? new ConfigurationBuilder().Build();
        services.AddSingleton(config);
        services.Configure<RoutesConfig>(config.GetSection("routes"));
        services.AddArrkCore();
        services.AddSingleton<INotificationEventProvider, DownloadNotificationEvents>();
        new NotificationModule().ConfigureServices(services, config);
        if (clients)
        {
            new DiscordNotificationModule().ConfigureServices(services, config);
            new WebhookNotificationModule().ConfigureServices(services, config);
        }
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Test]
    public async Task CoreModule_RegistersInboxService()
    {
        using ServiceProvider provider = Build(clients: false);

        INotificationService service = provider.GetRequiredService<INotificationService>();

        await Assert.That(service).IsTypeOf<NotificationService>();
        await Assert.That(provider.GetServices<INotificationClient>()).IsEmpty();
    }

    [Test]
    public async Task Registry_KnowsTheDownloadEventCatalogue()
    {
        using ServiceProvider provider = Build();

        NotificationEventRegistry registry = provider.GetRequiredService<NotificationEventRegistry>();

        await Assert.That(registry.IsKnown(DownloadNotificationTopics.DownloadCompleted)).IsTrue();
        await Assert.That(registry.IsKnown(DownloadNotificationTopics.TorrentStriked)).IsTrue();
    }

    [Test]
    public async Task DeliveryModule_RegistersBothChannels()
    {
        using ServiceProvider provider = Build();

        List<INotificationClient> channels = provider.GetServices<INotificationClient>().ToList();

        await Assert.That(channels.Count).IsEqualTo(2);
        await Assert.That(channels.Select(c => c.Name)).Contains(DiscordNotificationClient.ChannelName);
        await Assert.That(channels.Select(c => c.Name)).Contains(WebhookNotificationClient.ChannelName);
    }

    [Test]
    public async Task CoreModule_BindsNotificationConfig()
    {
        Dictionary<string, string?> values = new()
        {
            ["notifications:discord:webhookUrl"] = "https://discord.example/hook",
            ["notifications:discord:enabled"] = "true",
            ["notifications:discord:routing:media"] = "https://discord.example/media",
            ["notifications:discord:exclude:0"] = "system.health.network.speedtest.completed",
            ["notifications:discord:exclude:1"] = "downloads.progress",
            ["notifications:webhook:enabled"] = "true",
            ["notifications:webhook:url"] = "https://example.com/hook",
            ["notifications:webhook:routing:system"] = "https://example.com/system",
            ["notifications:webhook:exclude:0"] = "media.*",
            ["notifications:retentionDays"] = "30",
            ["notifications:topics:system.health"] = "false",
            ["notifications:topics:downloads.progress"] = "true"
        };

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using ServiceProvider provider = Build(configuration);

        NotificationConfig config = provider.GetRequiredService<IOptionsMonitor<NotificationConfig>>().CurrentValue;
        DiscordNotificationConfig discord = provider.GetRequiredService<IOptionsMonitor<DiscordNotificationConfig>>().CurrentValue;
        WebhookConfig webhook = provider.GetRequiredService<IOptionsMonitor<WebhookConfig>>().CurrentValue;

        await Assert.That(discord.WebhookUrl).IsEqualTo("https://discord.example/hook");
        await Assert.That(discord.Routing["media"]).IsEqualTo("https://discord.example/media");
        await Assert.That(discord.Exclude).Contains("system.health.network.speedtest.completed");
        await Assert.That(discord.Exclude).Contains("downloads.progress");
        await Assert.That(webhook.Enabled).IsTrue();
        await Assert.That(webhook.Url).IsEqualTo("https://example.com/hook");
        await Assert.That(webhook.Routing["system"]).IsEqualTo("https://example.com/system");
        await Assert.That(webhook.Exclude).Contains("media.*");
        await Assert.That(config.RetentionDays).IsEqualTo(30);
        await Assert.That(config.Topics["system.health"]).IsFalse();
        await Assert.That(config.Topics["downloads.progress"]).IsTrue();
    }

    [Test]
    public async Task AddArrkCore_WrapsEventStoreWithNotifyingDecorator()
    {
        using ServiceProvider provider = Build();

        IEventStore store = provider.GetRequiredService<IEventStore>();
        INotificationService notificationService = provider.GetRequiredService<INotificationService>();

        await Assert.That(store).IsTypeOf<NotifyingEventStore>();
        await Assert.That(notificationService).IsTypeOf<NotificationService>();
    }

    [Test]
    public async Task DeliveryModule_ChannelsResolveWithHttpClientFactory()
    {
        using ServiceProvider provider = Build();

        foreach (INotificationClient channel in provider.GetServices<INotificationClient>())
        {
            await Assert.That(channel).IsNotNull();
        }
    }
}
