using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Events;
using Stackarr.Core.Notifications;
using Stackarr.Notifications.Discord;
using Stackarr.DownloadManagement;

namespace Stackarr.Notifications.Tests;

public class NotificationModuleTests
{
    static ServiceProvider Build(IConfiguration? configuration = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContextFactory<StackarrDbContext>(o => o.UseSqlite("Data Source=:memory:"));

        IConfiguration config = configuration ?? new ConfigurationBuilder().Build();
        services.AddSingleton(config);
        services.Configure<RoutesConfig>(config.GetSection("routes"));
        services.AddStackarrCore();
        services.AddSingleton<INotificationEventProvider, DownloadNotificationEvents>();
        new NotificationModule().ConfigureServices(services, config);
        new NotificationDeliveryModule().ConfigureServices(services, config);
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task CoreModule_RegistersInboxService()
    {
        using ServiceProvider provider = Build();

        INotificationService service = provider.GetRequiredService<INotificationService>();

        await Assert.That(service).IsTypeOf<NotificationService>();
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

        List<INotificationChannel> channels = provider.GetServices<INotificationChannel>().ToList();

        await Assert.That(channels.Count).IsEqualTo(2);
        await Assert.That(channels.Select(c => c.Name)).Contains(DiscordNotificationChannel.ChannelName);
        await Assert.That(channels.Select(c => c.Name)).Contains(GenericWebhookNotificationChannel.ChannelName);
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

        await Assert.That(config.Discord.WebhookUrl).IsEqualTo("https://discord.example/hook");
        await Assert.That(config.Discord.Routing["media"]).IsEqualTo("https://discord.example/media");
        await Assert.That(config.Discord.Exclude).Contains("system.health.network.speedtest.completed");
        await Assert.That(config.Discord.Exclude).Contains("downloads.progress");
        await Assert.That(config.Webhook.Enabled).IsTrue();
        await Assert.That(config.Webhook.Url).IsEqualTo("https://example.com/hook");
        await Assert.That(config.Webhook.Routing["system"]).IsEqualTo("https://example.com/system");
        await Assert.That(config.Webhook.Exclude).Contains("media.*");
        await Assert.That(config.RetentionDays).IsEqualTo(30);
        await Assert.That(config.Topics["system.health"]).IsFalse();
        await Assert.That(config.Topics["downloads.progress"]).IsTrue();
    }

    [Test]
    public async Task AddStackarrCore_WrapsEventStoreWithNotifyingDecorator()
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

        foreach (INotificationChannel channel in provider.GetServices<INotificationChannel>())
        {
            await Assert.That(channel).IsNotNull();
        }
    }
}
