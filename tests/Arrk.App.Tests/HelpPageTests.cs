using Arrk.App.Components.Pages;
using Arrk.App.Services.Ui;
using Arrk.Core.Documentation;
using Arrk.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace Arrk.App.Tests;

public class HelpPageTests
{
    sealed class InstalledProvider(params DocumentationDocument[] documents) : IDocumentationProvider
    {
        public IReadOnlyList<DocumentationDocument> Documents { get; } = documents;
    }

    sealed class PrerenderJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static help rendering must not require JavaScript.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    sealed class HelpNavigationManager : NavigationManager
    {
        public HelpNavigationManager() => Initialize("http://localhost/", "http://localhost/help");
    }

    static async Task<string> Render(DocumentationCatalog catalog, string? topic = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton(catalog);
        services.AddSingleton<IJSRuntime, PrerenderJsRuntime>();
        services.AddSingleton<NavigationManager, HelpNavigationManager>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            ParameterView parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Help.Topic)] = topic });
            HtmlRootComponent root = await renderer.RenderComponentAsync<Help>(parameters);
            return root.ToHtmlString();
        });
    }

    [Test]
    public async Task ListsEveryProviderAndRendersTheFullSelectedGuideWithoutAi()
    {
        string longText = string.Join(" ", Enumerable.Repeat("Complete documentation.", 300));
        DocumentationCatalog catalog = new([
            new InstalledProvider(new DocumentationDocument("core/start", "# Start\n\nGetting started.")),
            new InstalledProvider(new DocumentationDocument("plugin/guide", "# Plugin guide\n\n" + longText + "\n\n## Last section\n\nEnd of the full guide."))
        ]);

        string html = await Render(catalog, "plugin/guide");

        await Assert.That(html).Contains("/help?topic=core%2Fstart");
        await Assert.That(html).Contains("/help?topic=plugin%2Fguide");
        await Assert.That(html).Contains("<h1>Plugin guide</h1>");
        await Assert.That(html).Contains(longText);
        await Assert.That(html).Contains("End of the full guide.");
        await Assert.That(html).Contains("aria-current=\"page\"");
    }

    [Test]
    public async Task UnknownTopicDoesNotSilentlyShowAnotherGuide()
    {
        DocumentationCatalog catalog = new([new InstalledProvider(new DocumentationDocument("core/start", "# Start\n\nGetting started."))]);

        string html = await Render(catalog, "missing/topic");

        await Assert.That(html).Contains("That guide is not installed.");
        await Assert.That(html).DoesNotContain("<h1>Start</h1>");
    }

    [Test]
    public async Task EmptyCatalogHasAnExplanation()
    {
        string html = await Render(new DocumentationCatalog([]));

        await Assert.That(html).Contains("No guides are installed.");
    }

    [Test]
    public async Task HelpNavigationHasNoAdminOrFeatureGate()
    {
        NavigationEntry help = new AppNavigationProvider().Entries.Single(entry => entry.Href == "/help");

        await Assert.That(help.AdminOnly).IsFalse();
        await Assert.That(help.EnabledConfigPath).IsNull();
    }
}
