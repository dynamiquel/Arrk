using System.Reflection;
using System.Text;
using Arrk.Core.Documentation;
using Arrk.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Arrk.Core.Tests;

public class DocumentationTests
{
    sealed class Provider(params DocumentationDocument[] documents) : IDocumentationProvider
    {
        public IReadOnlyList<DocumentationDocument> Documents { get; } = documents;
    }

    // Exercises manifest loading without changing the test project's embedded-resource configuration.
    sealed class ResourceAssembly(IReadOnlyDictionary<string, string> resources) : Assembly
    {
        public override AssemblyName GetName() => new("Example.Plugin");
        public override string[] GetManifestResourceNames() => resources.Keys.ToArray();
        public override Stream? GetManifestResourceStream(string name) => resources.TryGetValue(name, out string? markdown)
            ? new MemoryStream(Encoding.UTF8.GetBytes(markdown)) : null;
    }

    static DocumentationCatalog CreateCatalog() => new(
    [
        new Provider(new DocumentationDocument("music/guide", "# Music\n\nLibrary and listening.\n\n## Play music\nUse the player with headphones.\n\n## Downloads\nAcquire albums.\n")),
        new Provider(new DocumentationDocument("plugin/guide", "# Plugin\n\nIndependent extension.\n\n## Headphones\nConfigure headphones here.\n"))
    ]);

    [Test]
    public async Task Providers_ComposeWithoutAi_AndSameFilenameIsNamespaced()
    {
        DocumentationCatalog catalog = CreateCatalog();
        await Assert.That(catalog.Topics.Select(t => t.Id).SequenceEqual(["music/guide", "plugin/guide"])).IsTrue();
        await Assert.That(catalog.Topics[0].Title).IsEqualTo("Music");
        await Assert.That(catalog.Topics[0].Summary).IsEqualTo("Library and listening.");
        await Assert.That(catalog.GetDocument("music/guide")!.Markdown).Contains("Acquire albums");
        await Assert.That(catalog.GetDocument("plugin/guide")!.Markdown).Contains("Independent extension");
        IReadOnlyList<DocumentationMatch> matches = catalog.Search("HEADPHONES");
        await Assert.That(matches.Count).IsEqualTo(2);
        await Assert.That(matches[0].Topic).IsEqualTo("plugin/guide");
        await Assert.That(matches[0].Section).IsEqualTo("headphones");
        await Assert.That(matches.SequenceEqual(catalog.Search("headphones"))).IsTrue();
        await Assert.That(catalog.Search().All(m => m.Section == "overview")).IsTrue();
        await Assert.That(catalog.Search(" ").Count).IsEqualTo(2);
    }

    [Test]
    public async Task Search_ClampsLimitsQueryAndExcerpts()
    {
        DocumentationDocument[] documents = Enumerable.Range(0, 12)
            .Select(i => new DocumentationDocument($"plugin/guide-{i}", "# Guide\n\n" + new string('x', 1000) + " headphones")).ToArray();
        DocumentationCatalog catalog = new([new Provider(documents)]);
        await Assert.That(catalog.Search("headphones", int.MaxValue).Count).IsEqualTo(8);
        await Assert.That(catalog.Search(limit: int.MaxValue).Count).IsEqualTo(8);
        await Assert.That(catalog.Search("headphones", -1).Count).IsEqualTo(1);
        await Assert.That(catalog.Search("headphones").All(m => m.Excerpt.Length <= 240)).IsTrue();
        await Assert.That(catalog.Search("absent").Count).IsEqualTo(0);
        await Assert.That(catalog.Search("!!!").Count).IsEqualTo(0);
        await Assert.That(catalog.Search(new string('z', 200) + " headphones").Count).IsEqualTo(0);
        await Assert.That(catalog.Search("headphones" + new string(' ', 200) + "absent")
            .SequenceEqual(catalog.Search("headphones"))).IsTrue();
    }

    [Test]
    public async Task Search_IgnoresQuestionWords_AndDoesNotMatchAcronymsInsideWords()
    {
        DocumentationCatalog catalog = new([new Provider(
            new("plugin/failures", "# Failed downloads\n\nThe downloads have failed.\n"),
            new("plugin/models", "# AI models\n\nChoose your provider.\n\n## Subtitles\nConfigure subtitles here.\n"))]);
        await Assert.That(catalog.Search("How do I use AI?").All(m => m.Topic == "plugin/models")).IsTrue();
        await Assert.That(catalog.Search("How do I use AI?").Count).IsGreaterThan(0);
        await Assert.That(catalog.Search("subtitle").Single().Section).IsEqualTo("subtitles");
        await Assert.That(catalog.Search("how do I").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Search_FindsBackupAndDeletionInstructions_DespiteOverlappingPluginDocumentation()
    {
        List<DocumentationDocument> documents = [];
        foreach (string file in Directory.GetFiles(RepoPaths.Combine("src"), "*.md", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "Documentation"))
        {
            string module = Directory.GetParent(Path.GetDirectoryName(file)!)!.Name.Replace("Arrk.", "").ToLowerInvariant();
            documents.Add(new(module + "/" + Path.GetFileNameWithoutExtension(file), File.ReadAllText(file)));
        }
        foreach (string file in Directory.GetFiles(RepoPaths.Combine("scripts", "DocumentationEvaluation", "Fixtures"), "*.md"))
            documents.Add(new(Path.GetFileNameWithoutExtension(file) + "/guide", File.ReadAllText(file)));
        DocumentationCatalog catalog = new([new Provider(documents.ToArray())]);

        DocumentationMatch backup = catalog.Search("Where are my settings backed up?").First();
        await Assert.That(backup.Topic).IsEqualTo("orchestration/setup");
        await Assert.That(backup.Section).IsEqualTo("storage-and-backups");
        await Assert.That(catalog.Search("How do I stop favourites being deleted?").Take(3)
            .Any(m => m.Topic == "medialifecycle/lifecycle" && m.Section == "review-before-enabling-deletion")).IsTrue();
        // A named plugin's similar operation must remain distinct from library cleanup.
        DocumentationMatch plugin = catalog.Search("Playlist Bridge disconnect revoke access").First();
        await Assert.That(plugin.Topic).IsEqualTo("playlist-bridge/guide");
        await Assert.That(plugin.Section).IsEqualTo("disconnect-and-revoke-access");
    }

    [Test]
    public async Task Read_IsTargetedAndFullSectionAndDocumentPreserveExactMarkdown()
    {
        string overview = "# Guide\r\n\r\nIntroduction.  \r\n\r\n";
        string section = "## Details\r\n\r\nKeep trailing spaces.  \r\n\r\n";
        string markdown = overview + section + "## Other\r\nNot included.\r\n";
        DocumentationCatalog catalog = new([new Provider(new DocumentationDocument("plugin/guide", markdown))]);
        await Assert.That(catalog.GetDocument("PLUGIN/GUIDE")!.Markdown).IsEqualTo(markdown);
        await Assert.That(catalog.Read("PLUGIN/GUIDE").Markdown).IsEqualTo(overview);
        await Assert.That(catalog.Read("plugin/guide", "DETAILS").Markdown).IsEqualTo(section);
        await Assert.That(catalog.ReadSection("plugin/guide", "details")).IsEqualTo(section);
        await Assert.That(catalog.ReadSection("plugin/guide", null)).IsEqualTo(overview);
        await Assert.That(catalog.Read("plugin/guide", offset: -1).Markdown).IsEqualTo(overview);
        await Assert.That(catalog.Read("plugin/guide", offset: int.MaxValue).Markdown).IsEqualTo("");
        await Assert.That(catalog.Read("plugin/guide", "unknown").Error).IsNotNull();
        await Assert.That(catalog.ReadSection("plugin/guide", "unknown")).IsNull();
    }

    [Test]
    public async Task Read_BoundsAndContinuationReconstructFullExactSection()
    {
        string section = "## Details\n" + new string('x', 9500) + "\n";
        DocumentationCatalog catalog = new([new Provider(new DocumentationDocument("plugin/guide", "# Guide\n\nIntro.\n\n" + section))]);
        DocumentationPage first = catalog.Read("plugin/guide", "details", maxCharacters: -1);
        await Assert.That(first.Markdown.Length).IsEqualTo(256);
        await Assert.That(first.NextOffset).IsEqualTo(256);
        DocumentationPage bounded = catalog.Read("plugin/guide", "details", maxCharacters: int.MaxValue);
        await Assert.That(bounded.Markdown.Length).IsEqualTo(4000);
        StringBuilder reconstructed = new();
        int offset = 0;
        do
        {
            DocumentationPage page = catalog.Read("plugin/guide", "details", offset);
            reconstructed.Append(page.Markdown);
            if (page.NextOffset is not int next)
                break;
            offset = next;
        } while (true);
        await Assert.That(reconstructed.ToString()).IsEqualTo(section);
        await Assert.That(catalog.ReadSection("plugin/guide", "details")).IsEqualTo(section);
        await Assert.That(catalog.Read("plugin/guide", "details", section.Length).NextOffset).IsNull();
    }

    [Test]
    public async Task DuplicateDocumentsAndSectionSlugs_FailFast()
    {
        await Assert.That(() => new DocumentationCatalog(
            [new Provider(new DocumentationDocument("plugin/guide", "# One")), new Provider(new DocumentationDocument("plugin/guide", "# Two"))]))
            .Throws<InvalidOperationException>();
        foreach (string markdown in new[] { "# Guide\n## Same!\nOne\n## Same?\nTwo", "# Guide\n## Overview\nCollision", "# Guide\n## !!!\nInvalid slug" })
        {
            await Assert.That(() => new DocumentationCatalog([new Provider(new DocumentationDocument("plugin/guide", markdown))]))
                .Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task PathsUrlsAndInvalidNames_AreNeverResolved()
    {
        DocumentationCatalog catalog = CreateCatalog();
        foreach (string id in new[] { "../music", "/etc/passwd", "https://example.com", "file:///etc/passwd", "music/../guide", "music%2fguide", "music\\guide", "music/guide\n", "music", "Music/guide", "music/Guide", "music/a_b" })
        {
            await Assert.That(() => new DocumentationCatalog([new Provider(new DocumentationDocument(id, "# Invalid"))]))
                .Throws<ArgumentException>();
        }
        foreach (string id in new[] { "../music", "https://example.com", "/etc/passwd", "music/../guide", "music/guide\n" })
        {
            await Assert.That(catalog.Read(id).Error).IsNotNull();
            await Assert.That(catalog.GetDocument(id)).IsNull();
            await Assert.That(catalog.ReadSection(id, "overview")).IsNull();
            await Assert.That(catalog.Read("music/guide", id).Error).IsNotNull();
        }
        await Assert.That(catalog.Read(null).Error).IsNotNull();
        await Assert.That(catalog.GetDocument(null)).IsNull();
    }

    [Test]
    public async Task FencedExamples_DoNotCreateSectionsOrDuplicateSlugs()
    {
        string markdown = "# Guide\n\nIntro\n\n```markdown\n## Details\n```\n\n## Details\nReal section.\n~~~markdown\n## Details\n~~~\n";
        DocumentationCatalog catalog = new([new Provider(new DocumentationDocument("plugin/guide", markdown))]);
        await Assert.That(catalog.Topics[0].Sections.Count).IsEqualTo(2);
        await Assert.That(catalog.ReadSection("plugin/guide", "details")).IsEqualTo("## Details\nReal section.\n~~~markdown\n## Details\n~~~\n");
    }

    [Test]
    public async Task Extension_LoadsOnlyOwnedMarkdown_IsIdempotentAndComposesWithPluginProviders()
    {
        ResourceAssembly assembly = new(new Dictionary<string, string>
        {
            ["Example.Plugin.Documentation.guide.md"] = "# Embedded\n\nResource content.\n",
            ["Example.Plugin.Guides.old.md"] = "# Ignored",
            ["Other.Plugin.Documentation.wrong.md"] = "# Ignored",
            ["Example.Plugin.Documentation.notes.txt"] = "Ignored"
        });
        ServiceCollection services = new();
        services.AddModuleDocumentation("plugin", assembly);
        services.AddModuleDocumentation("plugin", assembly);
        services.AddSingleton<IDocumentationProvider>(new Provider(new DocumentationDocument("music/guide", "# Music\n\nResource content.\n")));
        using ServiceProvider provider = services.BuildServiceProvider();
        DocumentationCatalog catalog = provider.GetRequiredService<DocumentationCatalog>();
        await Assert.That(provider.GetServices<IDocumentationProvider>().Count()).IsEqualTo(2);
        await Assert.That(catalog.Topics.Count).IsEqualTo(2);
        await Assert.That(catalog.GetDocument("plugin/guide")!.Markdown).IsEqualTo("# Embedded\n\nResource content.\n");
        await Assert.That(catalog.Search("resource").Count).IsEqualTo(2);
        await Assert.That(ReferenceEquals(catalog, provider.GetRequiredService<DocumentationCatalog>())).IsTrue();
    }

    [Test]
    public async Task Extension_RejectsInvalidKeysAndResourceStems_AndAllowsEmptyModules()
    {
        ServiceCollection services = new();
        foreach (string key in new[] { "../plugin", "Plugin", "plugin/name", "", "plugin\n", "https://example.com" })
            await Assert.That(() => services.AddModuleDocumentation(key, typeof(DocumentationTests).Assembly)).Throws<ArgumentException>();
        ResourceAssembly invalid = new(new Dictionary<string, string> { ["Example.Plugin.Documentation.nested.guide.md"] = "# Invalid" });
        await Assert.That(() => services.AddModuleDocumentation("plugin", invalid)).Throws<InvalidOperationException>();
        services.AddModuleDocumentation("empty", typeof(DocumentationTests).Assembly);
        using ServiceProvider provider = services.BuildServiceProvider();
        await Assert.That(provider.GetRequiredService<DocumentationCatalog>().Topics.Count).IsEqualTo(0);
        await Assert.That(new DocumentationCatalog([]).Search().Count).IsEqualTo(0);
    }
}
