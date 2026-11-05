using Microsoft.Extensions.Configuration;
using Stackarr.Core.Configuration;
using Stackarr.Core.Notifications;
using Stackarr.TestSupport;

namespace Stackarr.Notifications.Tests;

public class NotificationStackInfoTests
{
    static NotificationStackInfo Info(string name, string publicUrl) => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StackName"] = name })
            .Build(),
        new MutableOptionsMonitor<RoutesConfig>(new RoutesConfig { PublicUrl = publicUrl }));

    [Test]
    public async Task Name_and_public_url_come_from_the_live_configuration()
    {
        NotificationStackInfo stack = Info("dev", "https://media.example/");

        await Assert.That(stack.Name).IsEqualTo("dev");
        await Assert.That(stack.PublicUrl).IsEqualTo("https://media.example");
    }

    [Test]
    public async Task Relative_links_are_made_absolute()
    {
        NotificationStackInfo stack = Info("dev", "https://media.example");

        await Assert.That(stack.AbsoluteUrl("/movies")).IsEqualTo("https://media.example/movies");
        await Assert.That(stack.AbsoluteUrl("shows")).IsEqualTo("https://media.example/shows");
        await Assert.That(stack.AbsoluteUrl("https://other.example/x")).IsEqualTo("https://other.example/x");
        await Assert.That(stack.AbsoluteUrl(null)).IsNull();
    }

    [Test]
    public async Task Missing_stack_name_falls_back_to_stackarr()
    {
        NotificationStackInfo stack = new(
            new ConfigurationBuilder().Build(),
            new MutableOptionsMonitor<RoutesConfig>(new RoutesConfig()));

        await Assert.That(stack.Name).IsEqualTo("Stackarr");
    }
}
