using System.Collections;
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
    [Arguments("http://example.com/hook", true)]
    [Arguments("", false)]
    [Arguments("/relative/hook", false)]
    [Arguments("file:///tmp/hook", false)]
    public async Task Client_modules_validate_their_own_routing_urls(string url, bool valid)
    {
        ServiceCollection services = new();
        IConfiguration config = new ConfigurationBuilder().Build();
        new DiscordNotificationModule().ConfigureServices(services, config);
        new WebhookNotificationModule().ConfigureServices(services, config);
        using ServiceProvider provider = services.BuildServiceProvider();
        DiscordNotificationConfig discord = new() { Routing = [new() { Topic = "media.#", Url = url }] };
        WebhookConfig webhook = new() { Routing = [new() { Topic = "media.#", Url = url }] };
        await Assert.That(provider.GetRequiredService<IValidator<DiscordNotificationConfig>>().Validate(discord).IsValid)
            .IsEqualTo(valid);
        await Assert.That(provider.GetRequiredService<IValidator<WebhookConfig>>().Validate(webhook).IsValid)
            .IsEqualTo(valid);
        discord.Routing[0].FromRoles = ["Admin"];
        webhook.Routing[0].FromRoles = ["Admin"];
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
                ["notifications:discord:routing:0:url"] = "https://discord.example/hook",
                ["notifications:discord:routing:1:url"] = "https://discord.example/admin",
                ["notifications:discord:routing:1:topic"] = "media.requests.#",
                ["notifications:discord:routing:1:fromRoles:0"] = "Admin",
                ["notifications:discord:directMessages:enabled"] = "true",
                ["notifications:discord:directMessages:botToken"] = "test-token",
                ["notifications:webhook:enabled"] = "true",
                ["notifications:webhook:routing:0:url"] = "https://webhook.example/hook",
                ["notifications:webhook:routing:1:url"] = "https://webhook.example/admin",
                ["notifications:webhook:routing:1:fromRoles:0"] = "Admin"
            }).Build();
        ServiceCollection services = new();
        new DiscordNotificationModule().ConfigureServices(services, configuration);
        new WebhookNotificationModule().ConfigureServices(services, configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        IOptionsMonitor<DiscordNotificationConfig> discord =
            provider.GetRequiredService<IOptionsMonitor<DiscordNotificationConfig>>();
        IOptionsMonitor<WebhookConfig> webhook = provider.GetRequiredService<IOptionsMonitor<WebhookConfig>>();
        await Assert.That(discord.CurrentValue.Routing[0].Url).IsEqualTo("https://discord.example/hook");
        await Assert.That(discord.CurrentValue.Routing[0].Topic).IsEqualTo("#");
        await Assert.That(discord.CurrentValue.Routing[0].FromRoles).IsEquivalentTo(new[] { "User" });
        await Assert.That(discord.CurrentValue.Routing[1].Url).IsEqualTo("https://discord.example/admin");
        await Assert.That(discord.CurrentValue.Routing[1].Topic).IsEqualTo("media.requests.#");
        await Assert.That(discord.CurrentValue.Routing[1].FromRoles).IsEquivalentTo(new[] { "Admin" });
        await Assert.That(discord.CurrentValue.DirectMessages.Enabled).IsTrue();
        await Assert.That(discord.CurrentValue.DirectMessages.BotToken).IsEqualTo("test-token");
        await Assert.That(webhook.CurrentValue.Routing[0].Url).IsEqualTo("https://webhook.example/hook");
        await Assert.That(webhook.CurrentValue.Routing[1].Url).IsEqualTo("https://webhook.example/admin");
        await Assert.That(webhook.CurrentValue.Routing[1].FromRoles).IsEquivalentTo(new[] { "Admin" });
        await Assert.That(webhook.CurrentValue.Enabled).IsTrue();
        await Assert.That(services.Count(s => s.ServiceType == typeof(INotificationClient))).IsEqualTo(2);

        configuration["notifications:discord:routing:0:url"] = "https://discord.example/changed";
        configuration["notifications:discord:routing:1:url"] = "https://discord.example/admin-changed";
        configuration.Reload();
        await Assert.That(discord.CurrentValue.Routing[0].Url).IsEqualTo("https://discord.example/changed");
        await Assert.That(discord.CurrentValue.Routing[1].Url).IsEqualTo("https://discord.example/admin-changed");
        await Assert.That(webhook.CurrentValue.Routing[0].Url).IsEqualTo("https://webhook.example/hook");
    }

    [Test]
    public async Task Transport_config_sections_render_under_existing_notifications_yaml_path()
    {
        ConfigSectionRegistry registry = new();
        registry.Register(new ConfigSectionProvider<NotificationConfig>("notifications", "Notifications"));
        foreach (IConfigSectionProvider section in new DiscordNotificationModule().GetConfigSections()
                     .Concat(new WebhookNotificationModule().GetConfigSections()))
            registry.Register(section);

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["notifications:discord:routing:0:topic"] = "media.requests.#",
            ["notifications:discord:routing:0:url"] = "https://discord.example/admin",
            ["notifications:discord:routing:0:fromRoles:0"] = "Admin",
            ["notifications:webhook:routing:0:topic"] = "media.requests.#",
            ["notifications:webhook:routing:0:url"] = "https://webhook.example/staff",
            ["notifications:webhook:routing:0:fromRoles:0"] = "Admin",
            ["notifications:webhook:routing:0:fromRoles:1"] = "Curator"
        }).Build();
        string yaml = YamlConfigSerializer.RenderFullConfig(configuration, registry);
        Dictionary<string, object?> root = ConfigFormBuilder.ParseYaml(yaml);
        Dictionary<string, object?> notifications = (Dictionary<string, object?>)root["notifications"]!;

        await Assert.That(notifications.ContainsKey("retentionDays")).IsTrue();
        await Assert.That(notifications.ContainsKey("topics")).IsTrue();
        await Assert.That(notifications.ContainsKey("discord")).IsTrue();
        await Assert.That(notifications.ContainsKey("webhook")).IsTrue();
        await Assert.That(root.ContainsKey("notifications:discord")).IsFalse();
        await Assert.That(root.ContainsKey("notifications:webhook")).IsFalse();
        foreach (string transport in new[] { "discord", "webhook" })
        {
            Dictionary<string, object?> config = (Dictionary<string, object?>)notifications[transport]!;
            IList routing = (IList)config["routing"]!;
            await Assert.That(routing.Count).IsEqualTo(1);
            Dictionary<string, object?> route = (Dictionary<string, object?>)routing[0]!;
            await Assert.That(route["topic"]).IsEqualTo("media.requests.#");
            IList roles = (IList)route["fromRoles"]!;
            await Assert.That(roles.Count).IsEqualTo(transport == "discord" ? 1 : 2);
            await Assert.That(roles[0]).IsEqualTo("Admin");
            await Assert.That(config.ContainsKey("roleRouting")).IsFalse();
            await Assert.That(config.ContainsKey("webhookUrl")).IsFalse();
            await Assert.That(config.ContainsKey("url")).IsFalse();
        }
    }

    [Test]
    [Arguments("discord")]
    [Arguments("webhook")]
    public async Task Route_editor_defaults_new_roles_to_User_without_overwriting_explicit_roles(string transport)
    {
        ConfigSectionRegistry registry = new();
        foreach (IConfigSectionProvider section in new DiscordNotificationModule().GetConfigSections()
                     .Concat(new WebhookNotificationModule().GetConfigSections()))
            registry.Register(section);
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        ConfigFormBuilder builder = new(new ConfigSchemaGenerator(registry, services), registry);
        ConfigFormModel model = builder.Build($$"""
            notifications:
              {{transport}}:
                routing:
                  - topic: media.requests.new
                    url: https://example.com/admin
                    fromRoles: [Admin]
                  - topic: media.requests.new
                    url: https://example.com/disabled
                    fromRoles: []
            """);
        ConfigArrayNode routing = (ConfigArrayNode)model.FindSection($"notifications:{transport}")!
            .Children.Single(n => n.Key == "routing");
        ConfigObjectNode newRoute = (ConfigObjectNode)routing.ItemPrototype!.Clone();
        ConfigArrayNode defaultRoles = (ConfigArrayNode)newRoute.Children.Single(n => n.Key == "fromRoles");
        ConfigArrayNode adminRoles = (ConfigArrayNode)((ConfigObjectNode)routing.Items[0]).Children.Single(n => n.Key == "fromRoles");
        ConfigArrayNode emptyRoles = (ConfigArrayNode)((ConfigObjectNode)routing.Items[1]).Children.Single(n => n.Key == "fromRoles");

        await Assert.That(defaultRoles.Items.Cast<ConfigScalarNode>().Select(n => n.Value)).IsEquivalentTo(new object?[] { "User" });
        await Assert.That(adminRoles.Items.Cast<ConfigScalarNode>().Select(n => n.Value)).IsEquivalentTo(new object?[] { "Admin" });
        await Assert.That(emptyRoles.Items).IsEmpty();
    }

    [Test]
    [Arguments("discord")]
    [Arguments("webhook")]
    public async Task Shared_clients_deliver_every_matching_topic_and_role_route_without_personal_fallback(string transport)
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
        List<NotificationRoute> routing =
        [
            new() { Url = $"{url}/public" },
            new() { Topic = "test.warning", Url = $"{url}/admin", FromRoles = ["Admin"] },
            new() { Topic = "test.warning", Url = $"{url}/staff", FromRoles = ["Admin", "Curator"] },
            new() { Topic = "test.#", Url = $"{url}/overlapping", FromRoles = ["Admin"] },
            new() { Topic = "media.requests.#", Url = $"{url}/unmatched", FromRoles = ["Admin"] }
        ];
        DiscordNotificationConfig discord = new() { Routing = routing };
        WebhookConfig webhook = new() { Enabled = true, Routing = routing };
        INotificationClient client = transport == "discord"
            ? TestNotifications.DiscordChannel(db, discord)
            : new WebhookNotificationClient(TestHttp.CreateFactory(), new MutableOptionsMonitor<WebhookConfig>(webhook),
                TestCatalog.Registry(), TestNotifications.StackInfo(), NullLogger<WebhookNotificationClient>.Instance);
        NotificationMessage message = new() { Key = TestNotificationTopics.Warning, Title = "Admin alert", Body = "Needs attention" };

        List<NotificationDeliveryResult> role = await client.SendAsync(message, new(null, "aDmIn")).CollectAsync();
        await Assert.That(role.Count).IsEqualTo(3);
        await Assert.That(role.All(r => r.Success && !r.Skipped)).IsTrue();
        foreach (NotificationRecipient recipient in new NotificationRecipient[] { new(null, "Unknown"), new(7), new(7, "Admin"), new(7, "User") })
            await Assert.That((await client.SendAsync(message, recipient).SingleResultAsync()).Skipped).IsTrue();
        NotificationDeliveryResult curator = await client.SendAsync(message, new(null, "cUrAtOr")).SingleResultAsync();
        await Assert.That(curator.Success && !curator.Skipped).IsTrue();
        NotificationDeliveryResult everyone = await client.SendAsync(message, NotificationRecipient.Everyone).SingleResultAsync();
        await Assert.That(everyone.Success && !everyone.Skipped).IsTrue();
        await Assert.That(string.Join(",", received.Select(r => r.Path)))
            .IsEqualTo("/admin,/staff,/overlapping,/staff,/public");
        await Assert.That(received.All(r => r.Body.Contains("Admin alert") && r.Body.Contains("Needs attention"))).IsTrue();

        NotificationDeliveryResult noMatch = await client.SendAsync(message with { Key = "unmatched.topic" }, new(null, "Admin")).SingleResultAsync();
        await Assert.That(noMatch.Skipped).IsTrue();
        routing[1].Url = $"{url}/changed";
        await client.SendAsync(message, new(null, "Admin")).CollectAsync();
        await Assert.That(received[^3].Path).IsEqualTo("/changed");
        discord.Exclude.Add("test.#");
        webhook.Exclude.Add("test.#");
        await Assert.That((await client.SendAsync(message, new(null, "Admin")).SingleResultAsync()).Skipped).IsTrue();
        discord.Exclude.Clear();
        webhook.Exclude.Clear();
        discord.Enabled = false;
        webhook.Enabled = false;
        await Assert.That((await client.SendAsync(message, new(null, "Admin")).SingleResultAsync()).Skipped).IsTrue();
        await Assert.That(received.Count).IsEqualTo(8);
    }

    [Test]
    [Arguments("discord")]
    [Arguments("webhook")]
    public async Task Shared_clients_yield_failures_and_successes_independently_and_preserve_success_before_cancellation(string transport)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using WebApplication server = builder.Build();
        List<string> received = [];
        server.MapPost("/{route}", async (HttpContext context) =>
        {
            received.Add(context.Request.Path.Value!);
            context.Response.StatusCode = context.Request.Path == "/failure"
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status204NoContent;
            if (context.Response.StatusCode == StatusCodes.Status400BadRequest)
                await context.Response.WriteAsync("rejected");
        });
        await server.StartAsync();
        string url = server.Urls.Single();
        await using TestDatabase db = new();
        List<NotificationRoute> routing =
        [
            new() { Url = $"{url}/failure" },
            new() { Url = $"{url}/success" },
            new() { Url = $"{url}/success" }
        ];
        INotificationClient client = transport == "discord"
            ? TestNotifications.DiscordChannel(db, new() { Routing = routing })
            : new WebhookNotificationClient(TestHttp.CreateFactory(),
                new MutableOptionsMonitor<WebhookConfig>(new() { Enabled = true, Routing = routing }),
                TestCatalog.Registry(), TestNotifications.StackInfo(), NullLogger<WebhookNotificationClient>.Instance);
        NotificationMessage message = new() { Key = TestNotificationTopics.Warning, Title = "Multiple endpoints", Body = "Independent outcomes" };

        List<NotificationDeliveryResult> results = await client.SendAsync(message, NotificationRecipient.Everyone).CollectAsync();
        await Assert.That(results.Count).IsEqualTo(3);
        await Assert.That(results[0].Success).IsFalse();
        await Assert.That(results[0].Error).Contains("400");
        await Assert.That(results[1].Success && !results[1].Skipped).IsTrue();
        await Assert.That(results[2].Success && !results[2].Skipped).IsTrue();
        await Assert.That(string.Join(",", received)).IsEqualTo("/failure,/success,/success");

        routing.RemoveAt(0);
        received.Clear();
        using CancellationTokenSource cancellation = new();
        // Supply the token through the enumerator to exercise EnumeratorCancellation too.
        await using IAsyncEnumerator<NotificationDeliveryResult> iterator =
            client.SendAsync(message, NotificationRecipient.Everyone).GetAsyncEnumerator(cancellation.Token);
        await Assert.That(await iterator.MoveNextAsync()).IsTrue();
        NotificationDeliveryResult completed = iterator.Current;
        await Assert.That(completed.Success && !completed.Skipped).IsTrue();
        cancellation.Cancel();
        bool cancelled = false;
        try
        {
            await iterator.MoveNextAsync();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        await Assert.That(cancelled).IsTrue();
        await Assert.That(completed.Success).IsTrue();
        await Assert.That(string.Join(",", received)).IsEqualTo("/success");
    }

    [Test]
    [Arguments("", true)]
    [Arguments("media.#", false)]
    public async Task Client_modules_require_nonempty_route_topics_and_roles(string topic, bool invalidTopic)
    {
        NotificationRoute route = new() { Topic = topic, Url = "https://example.com/hook", FromRoles = ["Admin"] };
        DiscordNotificationConfig discord = new() { Routing = [route] };
        WebhookConfig webhook = new() { Routing = [route] };
        await Assert.That(new DiscordNotificationConfigValidator().Validate(discord).IsValid).IsEqualTo(!invalidTopic);
        await Assert.That(new WebhookConfigValidator().Validate(webhook).IsValid).IsEqualTo(!invalidTopic);

        route.Topic = "#";
        route.FromRoles = [];
        await Assert.That(new DiscordNotificationConfigValidator().Validate(discord).IsValid).IsFalse();
        await Assert.That(new WebhookConfigValidator().Validate(webhook).IsValid).IsFalse();
        route.FromRoles = ["Admin", " "];
        await Assert.That(new DiscordNotificationConfigValidator().Validate(discord).IsValid).IsFalse();
        await Assert.That(new WebhookConfigValidator().Validate(webhook).IsValid).IsFalse();
    }
}
