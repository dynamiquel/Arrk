using Arrk.App.Services.Ui;

namespace Arrk.App.Tests;

public class DocumentationMarkdownTests
{
    [Test]
    public async Task RendersProseListsCodeAndTables()
    {
        string html = DocumentationMarkdown.Render("""
            # Guide

            Read **this** and `that`.

            - First
            - Second

            ```yaml
            enabled: true
            ```

            | Setting | Value |
            | --- | --- |
            | enabled | true |
            """);

        await Assert.That(html).Contains("<h1>Guide</h1>");
        await Assert.That(html).Contains("<strong>this</strong>");
        await Assert.That(html).Contains("<code>that</code>");
        await Assert.That(html).Contains("<li>First</li>");
        await Assert.That(html).Contains("<pre><code class=\"language-yaml\">");
        await Assert.That(html).Contains("<table>");
    }

    [Test]
    public async Task PreviewsArePlainTextAndTruncateAtWordBoundaries()
    {
        string shortPreview = DocumentationMarkdown.Preview("## Backups\n\nRead **this** [guide](https://example.com) and `config.yaml`.");
        string longPreview = DocumentationMarkdown.Preview(string.Join(" ", Enumerable.Repeat("Backup settings safely.", 30)));

        await Assert.That(shortPreview).IsEqualTo("Read this guide and config.yaml.");
        await Assert.That(longPreview.Length <= 241).IsTrue();
        await Assert.That(longPreview.EndsWith("…", StringComparison.Ordinal)).IsTrue();
        await Assert.That(longPreview[..^1].Split(' ').Last()).IsEqualTo("safely.");
        await Assert.That(DocumentationMarkdown.Preview(new string('x', 300))).IsEqualTo(new string('x', 240) + "…");
    }

    [Test]
    public async Task RawHtmlCannotCreateElementsOrAttributes()
    {
        string html = DocumentationMarkdown.Render("""
            <script>alert(1)</script>

            <img src=x onerror="alert(1)">

            <a href="javascript:alert(1)">unsafe</a>
            """);

        await Assert.That(html).DoesNotContain("<script");
        await Assert.That(html).DoesNotContain("<img");
        await Assert.That(html).DoesNotContain("<a ");
        await Assert.That(html).Contains("&lt;script&gt;");
    }

    [Test]
    [Arguments("javascript:alert%281%29")]
    [Arguments("JaVaScRiPt:alert%281%29")]
    [Arguments("jav&#x61;script:alert%281%29")]
    [Arguments("java&#x09;script:alert%281%29")]
    [Arguments("data:text/html;base64,PHNjcmlwdD4=")]
    [Arguments("vbscript:msgbox%281%29")]
    [Arguments("file:///etc/passwd")]
    [Arguments("//example.com/remote")]
    public async Task UnsafeLinkAndImageDestinationsAreNeutralized(string destination)
    {
        string html = DocumentationMarkdown.Render($"[Link]({destination})\n\n![Image]({destination})");

        await Assert.That(html).Contains("href=\"#\"");
        await Assert.That(html).Contains("src=\"#\"");
    }

    [Test]
    [Arguments("https://example.com/guide")]
    [Arguments("http://example.com/guide")]
    [Arguments("/help?topic=plugin/guide")]
    [Arguments("#settings")]
    [Arguments("guide.md")]
    [Arguments("mailto:help@example.com")]
    public async Task SafeLinkDestinationsArePreserved(string destination)
    {
        string html = DocumentationMarkdown.Render($"[Link]({destination})");

        await Assert.That(html).Contains($"href=\"{destination}\"");
    }

    [Test]
    public async Task ReferenceLinksAreAlsoChecked()
    {
        string html = DocumentationMarkdown.Render("[Link][unsafe]\n\n[unsafe]: javascript:alert%281%29");

        await Assert.That(html).Contains("href=\"#\"");
    }

    [Test]
    [Arguments("javascript:alert%281%29")]
    [Arguments("data:text/html;base64,PHNjcmlwdD4=")]
    public async Task UnsafeAutolinksAreAlsoChecked(string destination)
    {
        string html = DocumentationMarkdown.Render($"<{destination}>");

        await Assert.That(html).DoesNotContain($"href=\"{destination}\"");
    }
}
