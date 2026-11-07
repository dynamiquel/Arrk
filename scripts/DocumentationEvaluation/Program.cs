using System.Diagnostics;
using System.Text.Json;
using Arrk.Core.Documentation;

DirectoryInfo? directory = new(AppContext.BaseDirectory);
while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Arrk.slnx")))
    directory = directory.Parent;
string root = directory?.FullName ?? throw new InvalidOperationException("Run from the Arrk repository.");
string evaluation = Path.Combine(root, "scripts", "DocumentationEvaluation");
List<DocumentationDocument> documents = [];
foreach (string file in Directory.GetFiles(Path.Combine(root, "src"), "*.md", SearchOption.AllDirectories)
    .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "Documentation").Order(StringComparer.Ordinal))
{
    string module = Directory.GetParent(Path.GetDirectoryName(file)!)!.Name.Replace("Arrk.", "").ToLowerInvariant();
    documents.Add(new(module + "/" + Path.GetFileNameWithoutExtension(file), File.ReadAllText(file)));
}
foreach (string file in Directory.GetFiles(Path.Combine(evaluation, "Fixtures"), "*.md").Order(StringComparer.Ordinal))
    documents.Add(new(Path.GetFileNameWithoutExtension(file) + "/guide", File.ReadAllText(file)));

DocumentationCatalog catalog = new([new Corpus(documents)]);
List<Result> results = [];
foreach (string line in File.ReadLines(Path.Combine(evaluation, "cases.tsv")).Where(l => l.Length > 0 && !l.StartsWith('#')))
{
    string[] fields = line.Split('\t');
    if (fields.Length != 6)
        throw new InvalidOperationException("Expected id, group, language, question, reference keywords, relevant sections.");
    string[] relevant = fields[5] == "-" ? [] : fields[5].Split(';');
    foreach (string id in relevant)
    {
        string[] parts = id.Split('/');
        if (parts.Length != 3 || catalog.ReadSection(parts[0] + "/" + parts[1], parts[2]) is null)
            throw new InvalidOperationException($"{fields[0]}: unknown gold section {id}");
    }

    foreach ((string mode, string query) in new[] { ("raw", fields[3]), ("reference-keywords", fields[4]) })
    {
        long start = Stopwatch.GetTimestamp();
        IReadOnlyList<DocumentationMatch> matches = catalog.Search(query);
        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        string[] ids = matches.Select(m => m.Topic + "/" + m.Section).ToArray();
        int rank = Array.FindIndex(ids, id => relevant.Contains(id, StringComparer.Ordinal)) + 1;
        results.Add(new(fields[0], fields[1], fields[2], mode, query, relevant, rank, ids, milliseconds));
    }
}
if (results.Select(r => r.Id).Distinct().Count() * 2 != results.Count)
    throw new InvalidOperationException("Evaluation ids must be unique.");

Console.WriteLine($"Corpus: {documents.Count} documents, {catalog.Topics.Sum(t => t.Sections.Count)} sections, {documents.Sum(d => d.Markdown.Length)} characters.");
Console.WriteLine("Reference keywords are manually authored controls, NOT model-generated rewrites or measured AI answers.");
Console.WriteLine("Group | Mode | Answerable | Recall@1 | Recall@3 | Recall@8 | MRR@8 | Negative results");
foreach (IGrouping<(string Group, string Mode), Result> group in results.GroupBy(r => (r.Group, r.Mode)))
    Print(group.Key.Group, group.Key.Mode, group.ToArray());
foreach (IGrouping<string, Result> group in results.Where(r => r.Group == "languages" && r.Mode == "raw").GroupBy(r => r.Language))
    Print("language:" + group.Key, "raw", group.ToArray());
foreach (Result miss in results.Where(r => r.Relevant.Length > 0 && r.Rank == 0))
    Console.WriteLine($"MISS {miss.Id} [{miss.Mode}] {miss.Query} => {string.Join(", ", miss.Returned.Take(3))}");

if (args is [string output])
    File.WriteAllText(output, JsonSerializer.Serialize(new { documents = documents.Count,
        sections = catalog.Topics.Sum(t => t.Sections.Count), results }, new JsonSerializerOptions { WriteIndented = true }));
else if (args.Length > 0)
    throw new ArgumentException("Usage: dotnet run --project scripts/DocumentationEvaluation -- [results.json]");

static void Print(string group, string mode, Result[] results)
{
    Result[] positive = results.Where(r => r.Relevant.Length > 0).ToArray();
    int negatives = results.Count(r => r.Relevant.Length == 0);
    int falsePositives = results.Count(r => r.Relevant.Length == 0 && r.Returned.Length > 0);
    double mrr = positive.Length == 0 ? 0 : positive.Average(r => r.Rank == 0 ? 0 : 1d / r.Rank);
    Console.WriteLine($"{group} | {mode} | {positive.Length} | {positive.Count(r => r.Rank == 1)} | "
        + $"{positive.Count(r => r.Rank is > 0 and <= 3)} | {positive.Count(r => r.Rank > 0)} | {mrr:F3} | {falsePositives}/{negatives}");
}

sealed record Result(string Id, string Group, string Language, string Mode, string Query,
    string[] Relevant, int Rank, string[] Returned, double Milliseconds);

sealed class Corpus(IReadOnlyList<DocumentationDocument> documents) : IDocumentationProvider
{
    public IReadOnlyList<DocumentationDocument> Documents { get; } = documents;
}
