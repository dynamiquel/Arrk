using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Events;
using Arrk.Notifications;
using Arrk.Notifications.Webhook;
using Arrk.TestSupport;

namespace Arrk.Notifications.Tests;

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

    static WebhookNotificationClient Channel(WebhookConfig config) => new(
        TestHttp.CreateFactory(),
        new MutableOptionsMonitor<WebhookConfig>(config),
        TestCatalog.Registry(),
        TestNotifications.StackInfo(),
        NullLogger<WebhookNotificationClient>.Instance);

    static WebhookConfig WebhookConfig(string url, bool enabled = true, string? auth = null) => new()
    {
        Enabled = enabled, Url = url, AuthHeader = auth
    };

    [Test]
    public async Task SendAsync_RoutingOverride_PostsToCategoryUrl()
    {
        EchoServer? echo = await NotificationServers.GetEchoAsync();
        if (echo is null) return;

        WebhookConfig config = WebhookConfig($"{echo.BaseUrl}/generic-default");
        config.Routing["test.media"] = $"{echo.BaseUrl}/generic-media";

        NotificationMessage message = Message() with
        {
            Key = TestNotificationTopics.Media,
            Title = "Routed webhook alert"
        };

        WebhookNotificationClient channel = Channel(config);
        NotificationDeliveryResult result = await channel.SendAsync(message, NotificationRecipient.Global);

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

        WebhookNotificationClient channel = Channel(WebhookConfig($"{echo.BaseUrl}/generic-hook"));
        NotificationDeliveryResult result = await channel.SendAsync(Message(), NotificationRecipient.Global);

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

        WebhookNotificationClient channel = Channel(
            WebhookConfig($"{echo.BaseUrl}/auth-hook", auth: "Bearer secret-token"));
        NotificationDeliveryResult result = await channel.SendAsync(Message(), NotificationRecipient.Global);

        await Assert.That(result.Success).IsTrue();
        string logs = await echo.WaitForLogAsync("auth-hook", TimeSpan.FromSeconds(20));
        await Assert.That(logs).Contains("Bearer secret-token");
    }

    [Test]
    public async Task SendAsync_WithoutUrl_SkipsGlobalDelivery()
    {
        NotificationDeliveryResult result = await Channel(WebhookConfig(""))
            .SendAsync(Message(), NotificationRecipient.Global);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Skipped).IsTrue();
    }

    [Test]
    public async Task SendAsync_WhenDisabled_SkipsWithoutHttp()
    {
        WebhookNotificationClient channel = Channel(WebhookConfig("http://localhost:1/hook", enabled: false));

        NotificationDeliveryResult result = await channel.SendAsync(Message(), NotificationRecipient.Global);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Skipped).IsTrue();
    }

    [Test]
    public async Task SendAsync_PersonalRecipient_SkipsWithoutHttp()
    {
        WebhookNotificationClient channel = Channel(WebhookConfig("http://localhost:1/hook"));

        NotificationDeliveryResult result = await channel.SendAsync(Message(), new NotificationRecipient(7));

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Skipped).IsTrue();
    }

    [Test]
    public async Task SendAsync_ExcludedTopic_SkipsWithoutHttp()
    {
        int deadPort = Arrk.TestSupport.DockerTestContainer.GetAvailablePort();
        WebhookConfig config = WebhookConfig($"http://localhost:{deadPort}/hook");
        config.Exclude.Add("test.*");

        WebhookNotificationClient channel = Channel(config);
        NotificationDeliveryResult result = await channel.SendAsync(Message(), NotificationRecipient.Global);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Skipped).IsTrue();
    }

    [Test]
    public async Task SendAsync_UnreachableHost_ReturnsFailureNotThrow()
    {
        int deadPort = Arrk.TestSupport.DockerTestContainer.GetAvailablePort();
        WebhookNotificationClient channel = Channel(WebhookConfig($"http://localhost:{deadPort}/hook"));

        NotificationDeliveryResult result = await channel.SendAsync(Message(), NotificationRecipient.Global);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).Contains("request failed");
    }
}
