using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core.Configuration;
using Stackarr.Core.Events;
using Stackarr.Core.Notifications;
using Stackarr.TestSupport;

namespace Stackarr.Notifications.Tests;

/// <summary>
/// Exercises the generic JSON webhook channel against a real HTTP echo container, asserting the
/// exact payload and optional Authorization header that reached the wire.
/// </summary>
[NotInParallel(NotificationServers.ParallelKey)]
public class GenericWebhookDeliveryTests
{
    static NotificationMessage Message() => new()
    {
        Key = TestNotificationTopics.Warning,
        Title = "Media deleted",
        Body = "Old movie removed",
        Severity = EventSeverity.Warning,
        Source = "1273221",
        SourceName = "Old Movie",
        Timestamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        Fields = [new("Reason", "Storage cleanup")]
    };

    static GenericWebhookNotificationChannel Channel(NotificationConfig config) => new(
        TestHttp.CreateFactory(),
        new MutableOptionsMonitor<NotificationConfig>(config),
        TestCatalog.Registry(),
        TestNotifications.StackInfo(),
        NullLogger<GenericWebhookNotificationChannel>.Instance);

    static NotificationConfig WebhookConfig(string url, bool enabled = true, string? auth = null) => new()
    {
        Webhook = new WebhookConfig { Enabled = enabled, Url = url, AuthHeader = auth }
    };

    [Test]
    public async Task SendAsync_RoutingOverride_PostsToCategoryUrl()
    {
        EchoServer? echo = await NotificationServers.GetEchoAsync();
        if (echo is null) return;

        NotificationConfig config = WebhookConfig($"{echo.BaseUrl}/generic-default");
        config.Webhook.Routing["test.media"] = $"{echo.BaseUrl}/generic-media";

        NotificationMessage message = Message() with
        {
            Key = TestNotificationTopics.Media,
            Title = "Routed webhook alert"
        };

        GenericWebhookNotificationChannel channel = Channel(config);
        NotificationDeliveryResult result = await channel.SendAsync(message);

        await Assert.That(result.Success).IsTrue();
        string logs = await echo.WaitForLogAsync("generic-media", TimeSpan.FromSeconds(20));
        await Assert.That(logs).Contains("generic-media");
        await Assert.That(logs).Contains("Routed webhook alert");
    }

    [Test]
    public async Task SendAsync_ToRealServer_PostsJsonDocument()
    {
        EchoServer? echo = await NotificationServers.GetEchoAsync();
        if (echo is null) return;

        GenericWebhookNotificationChannel channel = Channel(WebhookConfig($"{echo.BaseUrl}/generic-hook"));
        NotificationDeliveryResult result = await channel.SendAsync(Message());

        await Assert.That(result.Success).IsTrue();

        string logs = await echo.WaitForLogAsync("generic-hook", TimeSpan.FromSeconds(20));
        await Assert.That(logs).Contains("test.warning");
        await Assert.That(logs).Contains("Media deleted");
        await Assert.That(logs).Contains("Warning");
        await Assert.That(logs).Contains("Old Movie");
        await Assert.That(logs).Contains("Storage cleanup");
        // Generic payload carries topic + group + resolved style.
        await Assert.That(logs).Contains("\"topic\"");
        await Assert.That(logs).Contains("\"group\"");
        await Assert.That(logs).Contains("\"color\"");
        await Assert.That(logs).Contains("\"stack\"");
        await Assert.That(logs).Contains("\"test-stack\"");
        await Assert.That(logs).Contains("https://stack.example");
        await Assert.That(logs).Contains("⚠️");
    }

    [Test]
    public async Task SendAsync_WithAuthHeader_SendsAuthorization()
    {
        EchoServer? echo = await NotificationServers.GetEchoAsync();
        if (echo is null) return;

        GenericWebhookNotificationChannel channel = Channel(
            WebhookConfig($"{echo.BaseUrl}/auth-hook", auth: "Bearer secret-token"));
        NotificationDeliveryResult result = await channel.SendAsync(Message());

        await Assert.That(result.Success).IsTrue();
        string logs = await echo.WaitForLogAsync("auth-hook", TimeSpan.FromSeconds(20));
        await Assert.That(logs).Contains("Bearer secret-token");
    }

    [Test]
    public async Task IsConfigured_RequiresEnabledAndUrl()
    {
        await Assert.That(Channel(WebhookConfig("http://localhost:1/hook")).IsConfigured).IsTrue();
        await Assert.That(Channel(WebhookConfig("http://localhost:1/hook", enabled: false)).IsConfigured).IsFalse();
        await Assert.That(Channel(WebhookConfig("")).IsConfigured).IsFalse();
    }

    [Test]
    public async Task SendAsync_WhenDisabled_ReturnsFailure()
    {
        GenericWebhookNotificationChannel channel = Channel(WebhookConfig("", enabled: false));

        NotificationDeliveryResult result = await channel.SendAsync(Message());

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).Contains("not configured");
    }

    [Test]
    public async Task SendAsync_ExcludedTopic_SkipsWithoutHttp()
    {
        int deadPort = Stackarr.TestSupport.DockerTestContainer.GetAvailablePort();
        NotificationConfig config = WebhookConfig($"http://localhost:{deadPort}/hook");
        config.Webhook.Exclude.Add("test.*");

        GenericWebhookNotificationChannel channel = Channel(config);
        NotificationDeliveryResult result = await channel.SendAsync(Message());

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Skipped).IsTrue();
    }

    [Test]
    public async Task SendAsync_UnreachableHost_ReturnsFailureNotThrow()
    {
        int deadPort = Stackarr.TestSupport.DockerTestContainer.GetAvailablePort();
        GenericWebhookNotificationChannel channel = Channel(WebhookConfig($"http://localhost:{deadPort}/hook"));

        NotificationDeliveryResult result = await channel.SendAsync(Message());

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).Contains("request failed");
    }
}
