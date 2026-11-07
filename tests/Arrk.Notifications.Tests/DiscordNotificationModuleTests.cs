using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FluentValidation;
using Arrk.Core.Configuration;
using Arrk.Notifications;
using Arrk.Notifications.Discord;
using Arrk.Notifications.Webhook;
using Arrk.TestSupport;

namespace Arrk.Notifications.Tests;

public class DiscordNotificationModuleTests
{
    [Test]
    [Arguments("https://example.com/hook", true)]
    [Arguments("", true)]
    [Arguments("/relative/hook", false)]
    [Arguments("file:///tmp/hook", false)]
    public async Task Client_modules_validate_their_own_routing_urls(string url, bool valid)
    {
        ServiceCollection services = new();
        IConfiguration config = new ConfigurationBuilder().Build();
        new DiscordNotificationModule().ConfigureServices(services, config);
        new WebhookNotificationModule().ConfigureServices(services, config);
        using ServiceProvider provider = services.BuildServiceProvider();
        DiscordNotificationConfig discord = new() { Routing = new() { ["media.#"] = url } };
        WebhookConfig webhook = new() { Routing = new() { ["media.#"] = url } };
        await Assert.That(provider.GetRequiredService<IValidator<DiscordNotificationConfig>>().Validate(discord).IsValid)
            .IsEqualTo(valid);
        await Assert.That(provider.GetRequiredService<IValidator<WebhookConfig>>().Validate(webhook).IsValid)
            .IsEqualTo(valid);
        discord.Routing.Clear();
        webhook.Routing.Clear();
        discord.RoleRouting["Admin"] = url;
        webhook.RoleRouting["Admin"] = url;
        await Assert.That(provider.GetRequiredService<IValidator<DiscordNotificationConfig>>().Validate(discord).IsValid)
            .IsEqualTo(valid);
        await Assert.That(provider.GetRequiredService<IValidator<WebhookConfig>>().Validate(webhook).IsValid)
            .IsEqualTo(valid);
    }

    [Test]
    [Arguments("", "https://discord.com/api/v10", false)]
    [Arguments("test-token", "https://discord.com/api/v10", true)]
    [Arguments("test-token", "", false)]
    [Arguments("test-token", "/api/v10", false)]
    public async Task Enabled_direct_messages_require_a_token_and_http_api(string token, string api, bool valid)
    {
        DiscordNotificationConfig config = new() { DirectMessages = new() { Enabled = true, BotToken = token, ApiBaseUrl = api } };
        await Assert.That(new DiscordNotificationConfigValidator().Validate(config).IsValid).IsEqualTo(valid);
    }

    [Test]
    public async Task Transport_modules_bind_independent_nested_options_and_register_clients()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["notifications:retentionDays"] = "15",
                ["notifications:discord:webhookUrl"] = "https://discord.example/hook",
                ["notifications:discord:roleRouting:Admin"] = "https://discord.example/admin",
                ["notifications:discord:directMessages:enabled"] = "true",
                ["notifications:discord:directMessages:botToken"] = "test-token",
                ["notifications:webhook:enabled"] = "true",
                ["notifications:webhook:url"] = "https://webhook.example/hook",
                ["notifications:webhook:roleRouting:Admin"] = "https://webhook.example/admin"
            }).Build();
        ServiceCollection services = new();
        new DiscordNotificationModule().ConfigureServices(services, configuration);
        new WebhookNotificationModule().ConfigureServices(services, configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        IOptionsMonitor<DiscordNotificationConfig> discord =
            provider.GetRequiredService<IOptionsMonitor<DiscordNotificationConfig>>();
        IOptionsMonitor<WebhookConfig> webhook = provider.GetRequiredService<IOptionsMonitor<WebhookConfig>>();
        await Assert.That(discord.CurrentValue.WebhookUrl).IsEqualTo("https://discord.example/hook");
        await Assert.That(discord.CurrentValue.RoleRouting["Admin"]).IsEqualTo("https://discord.example/admin");
        await Assert.That(discord.CurrentValue.DirectMessages.Enabled).IsTrue();
        await Assert.That(discord.CurrentValue.DirectMessages.BotToken).IsEqualTo("test-token");
        await Assert.That(webhook.CurrentValue.Url).IsEqualTo("https://webhook.example/hook");
        await Assert.That(webhook.CurrentValue.RoleRouting["Admin"]).IsEqualTo("https://webhook.example/admin");
        await Assert.That(webhook.CurrentValue.Enabled).IsTrue();
        await Assert.That(services.Count(s => s.ServiceType == typeof(INotificationClient))).IsEqualTo(2);

        configuration["notifications:discord:webhookUrl"] = "https://discord.example/changed";
        configuration["notifications:discord:roleRouting:Admin"] = "https://discord.example/admin-changed";
        configuration.Reload();
        await Assert.That(discord.CurrentValue.WebhookUrl).IsEqualTo("https://discord.example/changed");
        await Assert.That(discord.CurrentValue.RoleRouting["Admin"]).IsEqualTo("https://discord.example/admin-changed");
        await Assert.That(webhook.CurrentValue.Url).IsEqualTo("https://webhook.example/hook");
    }

    [Test]
    public async Task Transport_config_sections_render_under_existing_notifications_yaml_path()
    {
        ConfigSectionRegistry registry = new();
        registry.Register(new ConfigSectionProvider<NotificationConfig>("notifications", "Notifications"));
        foreach (IConfigSectionProvider section in new DiscordNotificationModule().GetConfigSections()
                     .Concat(new WebhookNotificationModule().GetConfigSections()))
            registry.Register(section);

        string yaml = YamlConfigSerializer.RenderFullConfig(new ConfigurationBuilder().Build(), registry);
        Dictionary<string, object?> root = ConfigFormBuilder.ParseYaml(yaml);
        Dictionary<string, object?> notifications = (Dictionary<string, object?>)root["notifications"]!;

        await Assert.That(notifications.ContainsKey("retentionDays")).IsTrue();
        await Assert.That(notifications.ContainsKey("topics")).IsTrue();
        await Assert.That(notifications.ContainsKey("discord")).IsTrue();
        await Assert.That(notifications.ContainsKey("webhook")).IsTrue();
        await Assert.That(root.ContainsKey("notifications:discord")).IsFalse();
        await Assert.That(root.ContainsKey("notifications:webhook")).IsFalse();
    }

    [Test]
    [Arguments("discord")]
    [Arguments("webhook")]
    public async Task Shared_clients_route_roles_explicitly_without_global_or_personal_fallback(string transport)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using WebApplication server = builder.Build();
        List<(string Path, string Body)> received = [];
        server.MapPost("/{route}", async (HttpContext context) =>
        {
            using StreamReader reader = new(context.Request.Body);
            received.Add((context.Request.Path.Value!, await reader.ReadToEndAsync()));
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        });
        await server.StartAsync();
        string url = server.Urls.Single();
        await using TestDatabase db = new();
        Dictionary<string, string> roles = new() { ["Admin"] = $"{url}/admin", ["Curator"] = " " };
        Dictionary<string, string> topics = new() { ["test.#"] = $"{url}/topic" };
        DiscordNotificationConfig discord = new() { WebhookUrl = $"{url}/global", RoleRouting = roles, Routing = topics };
        WebhookConfig webhook = new() { Enabled = true, Url = $"{url}/global", RoleRouting = roles, Routing = topics };
        INotificationClient client = transport == "discord"
            ? TestNotifications.DiscordChannel(db, discord)
            : new WebhookNotificationClient(TestHttp.CreateFactory(), new MutableOptionsMonitor<WebhookConfig>(webhook),
                TestCatalog.Registry(), TestNotifications.StackInfo(), NullLogger<WebhookNotificationClient>.Instance);
        NotificationMessage message = new() { Key = TestNotificationTopics.Warning, Title = "Admin alert", Body = "Needs attention" };

        NotificationDeliveryResult role = await client.SendAsync(message, new(null, "aDmIn"));
        await Assert.That(role.Success && !role.Skipped).IsTrue();
        foreach (NotificationRecipient recipient in new NotificationRecipient[] { new(null, "Unknown"), new(null, "Curator"), new(7) })
            await Assert.That((await client.SendAsync(message, recipient)).Skipped).IsTrue();
        NotificationDeliveryResult global = await client.SendAsync(message, NotificationRecipient.Global);
        await Assert.That(global.Success && !global.Skipped).IsTrue();
        await Assert.That(received.Select(r => r.Path).ToArray()).IsEquivalentTo(new[] { "/admin", "/topic" });
        await Assert.That(received.All(r => r.Body.Contains("Admin alert") && r.Body.Contains("Needs attention"))).IsTrue();

        roles["Admin"] = $"{url}/changed";
        await Assert.That((await client.SendAsync(message, new(null, "Admin"))).Skipped).IsFalse();
        await Assert.That(received.Last().Path).IsEqualTo("/changed");
        discord.Exclude.Add("test.#");
        webhook.Exclude.Add("test.#");
        await Assert.That((await client.SendAsync(message, new(null, "Admin"))).Skipped).IsTrue();
        discord.Exclude.Clear();
        webhook.Exclude.Clear();
        discord.Enabled = false;
        webhook.Enabled = false;
        await Assert.That((await client.SendAsync(message, new(null, "Admin"))).Skipped).IsTrue();
        await Assert.That(received.Count).IsEqualTo(3);
    }
}
