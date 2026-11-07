using System.Text.RegularExpressions;

namespace Arrk.Core.Documentation;

/// <summary>
/// Indexes module and plugin documentation in memory. Caller input is only an index key;
/// it is never resolved as a file path, resource name or URL.
/// </summary>
public sealed class DocumentationCatalog
{
    public const int MaxResults = 8;
    public const int MaxReadCharacters = 4000;
    public const int MaxQueryCharacters = 200;

    static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "been", "being", "but", "by", "can", "could",
        "did", "do", "does", "for", "from", "had", "has", "have", "how", "i", "if", "in", "into",
        "is", "it", "its", "me", "my", "not", "of", "on", "or", "our", "should", "so", "some",
        "that", "the", "their", "them", "there", "these", "they", "this", "to", "up", "us", "was",
        "we", "were", "what", "when", "where", "which", "who", "why", "will", "with", "would", "you", "your"
    };

    sealed record Section(string Id, string Title, string Markdown);
    sealed record Guide(DocumentationDocument Document, DocumentationTopic Topic, IReadOnlyList<Section> Sections);

    readonly IReadOnlyList<Guide> _guides;

    public DocumentationCatalog(IEnumerable<IDocumentationProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        Dictionary<string, Guide> guides = new(StringComparer.Ordinal);
        foreach (DocumentationDocument document in providers.SelectMany(p => p.Documents))
        {
            if (!IsDocumentId(document.Id))
                throw new ArgumentException($"Invalid documentation id '{document.Id}'. Expected module/stem with lowercase names.", nameof(providers));
            ArgumentNullException.ThrowIfNull(document.Markdown);
            if (!guides.TryAdd(document.Id, Parse(document)))
                throw new InvalidOperationException($"Duplicate documentation id '{document.Id}'.");
        }

        _guides = Array.AsReadOnly(guides.Values.OrderBy(g => g.Topic.Id, StringComparer.Ordinal).ToArray());
        Topics = Array.AsReadOnly(_guides.Select(g => g.Topic).ToArray());
    }

    public IReadOnlyList<DocumentationTopic> Topics { get; }

    /// <summary>Full original Markdown for human rendering; no truncation or normalization.</summary>
    public DocumentationDocument? GetDocument(string? id) => Find(id)?.Document;

    /// <summary>Full exact section, including its heading and whitespace, for opt-in resource reads.</summary>
    public string? ReadSection(string? topic, string? section) => Select(Find(topic), section)?.Markdown;

    /// <summary>Empty query lists topic overviews; nonempty queries rank section matches.</summary>
    public IReadOnlyList<DocumentationMatch> Search(string? query = null, int limit = MaxResults)
    {
        string boundedQuery = (query ?? "").Trim();
        boundedQuery = boundedQuery[..Math.Min(boundedQuery.Length, MaxQueryCharacters)];
        string[] terms = Regex.Split(boundedQuery.ToLowerInvariant(), "[^\\p{L}\\p{N}]+")
            .Where(t => t.Length > 1 && !StopWords.Contains(t)).Distinct(StringComparer.Ordinal).Take(12).ToArray();

        return _guides.SelectMany(g => g.Sections.Select(s => new
            {
                Guide = g,
                Section = s,
                Score = terms.Sum(t =>
                    (ContainsTerm(g.Topic.Title, t) ? 4 : 0)
                    + (ContainsTerm(s.Title, t) ? 8 : 0)
                    + (ContainsTerm(s.Markdown, t) ? 1 : 0))
            }))
            .Where(m => boundedQuery.Length == 0 ? m.Section.Id == "overview" : terms.Length > 0 && m.Score > 0)
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Guide.Topic.Id, StringComparer.Ordinal)
            .ThenBy(m => m.Section.Id, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, MaxResults))
            .Select(m => new DocumentationMatch(m.Guide.Topic.Id, m.Guide.Topic.Title,
                m.Section.Id, m.Section.Title, Excerpt(m.Section.Markdown, terms)))
            .ToArray();
    }

    /// <summary>Reads one bounded section; omitting the section selects only the overview.</summary>
    public DocumentationPage Read(string? topic, string? section = null, int offset = 0,
        int maxCharacters = MaxReadCharacters)
    {
        Guide? guide = Find(topic);
        if (guide is null)
            return new("", "", "", "", null, "Unknown topic. Use a topic id from the documentation index.");

        Section? selected = Select(guide, section);
        if (selected is null)
            return new(guide.Topic.Id, guide.Topic.Title, "", "", null, "Unknown section. Use a section id from the topic index.");

        int start = Math.Clamp(offset, 0, selected.Markdown.Length);
        int count = Math.Min(Math.Clamp(maxCharacters, 256, MaxReadCharacters), selected.Markdown.Length - start);
        int end = start + count;
        return new(guide.Topic.Id, guide.Topic.Title, selected.Id,
            selected.Markdown.Substring(start, count), end < selected.Markdown.Length ? end : null);
    }

    internal static bool IsName(string? name) => name is not null
        && Regex.IsMatch(name, "\\A[a-z0-9]+(?:-[a-z0-9]+)*\\z", RegexOptions.CultureInvariant);

    // Prefix matching supports searches such as "subtitle" → "subtitles", but "AI" must not
    // match inside "failed". Short acronyms require an exact word boundary on both sides.
    static bool ContainsTerm(string text, string term) => Regex.IsMatch(text,
        @"(?<![\p{L}\p{N}])" + Regex.Escape(term) + (term.Length < 4 ? @"(?![\p{L}\p{N}])" : ""),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static bool IsDocumentId(string? id) => id?.Split('/') is [string module, string stem]
        && IsName(module) && IsName(stem);

    Guide? Find(string? id) => _guides.FirstOrDefault(g => string.Equals(g.Topic.Id, id, StringComparison.OrdinalIgnoreCase));

    static Section? Select(Guide? guide, string? section) => guide?.Sections.FirstOrDefault(s =>
        string.Equals(s.Id, string.IsNullOrWhiteSpace(section) ? "overview" : section, StringComparison.OrdinalIgnoreCase));

    static Guide Parse(DocumentationDocument document)
    {
        string markdown = document.Markdown;
        List<(int Offset, string Title)> headings = [];
        char fence = '\0';
        int fenceLength = 0;
        int offset = 0;
        foreach (string line in markdown.Split('\n'))
        {
            string trimmed = line.Trim();
            char marker = trimmed.Length > 0 ? trimmed[0] : '\0';
            int markerLength = trimmed.TakeWhile(c => c == marker).Count();
            if (marker is '`' or '~' && markerLength >= 3)
            {
                if (fence == '\0')
                {
                    fence = marker;
                    fenceLength = markerLength;
                }
                else if (marker == fence && markerLength >= fenceLength && trimmed.Length == markerLength)
                    fence = '\0';
            }
            else if (fence == '\0' && line.StartsWith("## ", StringComparison.Ordinal))
                headings.Add((offset, line[3..].Trim()));
            offset += line.Length + 1;
        }

        string introduction = markdown[..(headings.Count > 0 ? headings[0].Offset : markdown.Length)];
        string[] introductionLines = introduction.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string title = introductionLines.FirstOrDefault()?.TrimStart('#', ' ').TrimEnd('\r') ?? document.Id;
        string summary = introductionLines.Skip(1).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        List<Section> sections = [new("overview", "Overview", introduction)];
        HashSet<string> ids = new(StringComparer.Ordinal) { "overview" };
        for (int index = 0; index < headings.Count; index++)
        {
            (int start, string heading) = headings[index];
            string id = Regex.Replace(heading.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (!IsName(id))
                throw new InvalidOperationException($"Invalid section id in documentation '{document.Id}': '{heading}'.");
            if (!ids.Add(id))
                throw new InvalidOperationException($"Duplicate section id '{id}' in documentation '{document.Id}'.");
            int end = index + 1 < headings.Count ? headings[index + 1].Offset : markdown.Length;
            sections.Add(new(id, heading, markdown[start..end]));
        }

        DocumentationTopic topic = new(document.Id, title, summary,
            Array.AsReadOnly(sections.Select(s => new DocumentationSection(s.Id, s.Title)).ToArray()));
        return new(document, topic, sections.AsReadOnly());
    }

    static string Excerpt(string markdown, string[] terms)
    {
        string text = Regex.Replace(markdown, "\\s+", " ");
        int match = terms.Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0).DefaultIfEmpty(0).Min();
        int start = Math.Max(0, match - 50);
        return text.Substring(start, Math.Min(240, text.Length - start));
    }
}
