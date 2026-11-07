using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Arrk.Ai;

/// <summary>The tools chosen for one turn, plus the friendly labels used in the UI.</summary>
public sealed record AssistantToolSet(
    IReadOnlyList<AITool> Tools,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> PackKeys);

/// <summary>
/// Composes the tool packs and media sources contributed by modules. Selection is cheap
/// and deterministic: a pack with no keywords is always offered, one with keywords is offered only
/// when the user's message mentions one. That keeps the tool schema small without spending a model
/// call on routing, and makes the selection testable without a network.
/// </summary>
public sealed class AssistantRegistry(
    IEnumerable<IAssistantTool> packs,
    IEnumerable<IAssistantMediaSource> mediaSources)
{
    /// <summary>Upper bound on the number of function schemas sent to the model in one turn.</summary>
    public const int MaxTools = 20;

    readonly IReadOnlyList<IAssistantTool> _packs = packs.ToList();

    public IReadOnlyList<IAssistantMediaSource> MediaSources { get; } =
        mediaSources.OrderBy(s => s.Key, StringComparer.Ordinal).ToList();

    public IReadOnlyList<IAssistantMediaSource> AvailableMediaSources =>
        MediaSources.Where(s => s.IsAvailable).ToList();

    /// <summary>Whether anything at all can answer; the assistant page uses this for its empty state.</summary>
    public bool HasAvailableTools => _packs.Any(p => p.IsAvailable);

    /// <summary>Packs the caller can actually use, for the system prompt's capability line.</summary>
    public IReadOnlyList<IAssistantTool> AvailablePacks(AssistantToolContext? context = null) =>
        _packs
            .Where(p => p.IsAvailable && (context is null || context.CanAccess(p.Access)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>All authorized tools, without chat routing or its schema cap (for MCP clients).</summary>
    public IReadOnlyList<AIFunction> AllTools(AssistantToolContext context) =>
        AvailablePacks(context).SelectMany(pack => CreateSafely(pack, context))
            .OfType<AIFunction>().DistinctBy(tool => tool.Name).ToList();

    static IReadOnlyList<AITool> CreateSafely(IAssistantTool pack, AssistantToolContext context)
    {
        try { return pack.CreateTools(context); }
        catch { return []; }
    }

    public AssistantToolSet Build(AssistantToolContext context, string userText, bool discovery = false)
    {
        List<IAssistantTool> selected = [];

        // Always-on packs first, so they win the cap over packs triggered by a stray keyword.
        foreach (IAssistantTool pack in _packs.OrderBy(p => p.Keywords.Count == 0 ? 0 : 1)
                     .ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            if (selected.Count >= MaxTools)
                break;
            if (!pack.IsAvailable || !context.CanAccess(pack.Access))
                continue;
            if (pack.Keywords.Count > 0 && !Mentions(userText, pack.Keywords))
                continue;
            selected.Add(pack);
        }

        List<AITool> tools = [];
        Dictionary<string, string> labels = new(StringComparer.Ordinal);
        if (discovery)
        {
            tools.Add(AIFunctionFactory.Create(
                (string? pack = null) => AvailablePacks(context)
                    .Where(p => string.IsNullOrWhiteSpace(pack) || p.Key.Equals(pack, StringComparison.OrdinalIgnoreCase))
                    .Select(p => new
                    {
                        pack = p.Key, p.Description,
                        tools = string.IsNullOrWhiteSpace(pack) ? null : CreateSafely(p, context)
                            .OfType<AIFunction>().Select(f => new { f.Name, f.Description, parameters = f.JsonSchema }).ToList()
                    }).ToList(),
                "discover_tools", "List available tool packs; pass a pack key to read its tool names and parameter schemas when a needed tool is not offered."));
            tools.Add(AIFunctionFactory.Create(
                async (string name, JsonElement arguments, CancellationToken ct) =>
                {
                    AIFunction? function = AllTools(context).FirstOrDefault(f => f.Name == name);
                    if (function is null)
                        return JsonSerializer.SerializeToElement(new { error = "Tool unavailable or not permitted." });
                    Dictionary<string, object?> values = arguments.Deserialize<Dictionary<string, object?>>() ?? [];
                    return await function.InvokeAsync(new AIFunctionArguments(values), ct);
                },
                "invoke_tool", "Invoke a tool found with discover_tools using its exact name and JSON arguments. The same permissions and confirmation rules apply."));
            labels["discover_tools"] = "Found tools";
            labels["invoke_tool"] = "Used tool";
        }
        foreach (IAssistantTool pack in selected)
        {
            foreach (AITool tool in CreateSafely(pack, context))
            {
                if (tools.Count >= MaxTools)
                    break;
                tools.Add(tool);
                if (tool is AIFunction function)
                    labels[function.Name] = pack.Name;
            }
        }

        return new AssistantToolSet(tools, labels, selected.Select(p => p.Key).ToList());
    }

    static bool Mentions(string text, IReadOnlyList<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (string keyword in keywords)
        {
            if (!string.IsNullOrWhiteSpace(keyword)
                && text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
