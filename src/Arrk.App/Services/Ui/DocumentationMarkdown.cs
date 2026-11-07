using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Arrk.App.Services.Ui;

/// <summary>Renders installed guides, never raw HTML or executable link destinations.</summary>
public static class DocumentationMarkdown
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .Build();

    public static string Render(string markdown)
    {
        MarkdownDocument document = Markdown.Parse(markdown, Pipeline);
        foreach (LinkInline link in document.Descendants<LinkInline>())
        {
            if (!IsSafeDestination(link.Url, link.IsImage))
                link.Url = "#";
        }

        foreach (AutolinkInline link in document.Descendants<AutolinkInline>())
        {
            if (!IsSafeDestination(link.Url, image: false))
                link.Url = "#";
        }

        return document.ToHtml(Pipeline);
    }

    static bool IsSafeDestination(string? destination, bool image)
    {
        if (string.IsNullOrWhiteSpace(destination))
            return true;

        string url = destination.Trim();
        if (url.Any(char.IsControl) || url.Contains('\\') || url.StartsWith("//", StringComparison.Ordinal))
            return false;

        // Uri treats root-relative paths as absolute file URIs; browsers treat them as local routes.
        if (url.StartsWith('/'))
            return true;

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? absolute))
            return absolute.Scheme is "https" or "http" || (!image && absolute.Scheme == "mailto");

        // A colon before the first path/query/fragment separator is a scheme, not a local path.
        return !url.Split('/', '?', '#')[0].Contains(':');
    }
}
