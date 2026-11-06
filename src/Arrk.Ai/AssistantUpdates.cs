namespace Arrk.Ai;

/// <summary>The signed-in caller an assistant question runs as.</summary>
public sealed record AssistantUser(int Id, string Username, bool IsAdmin);

/// <summary>Streamed updates from <see cref="AssistantService.StreamAsync"/>.</summary>
public abstract record AssistantUpdate;

/// <summary>A chunk of the answer, appended in order.</summary>
public sealed record AssistantTextDelta(string Text) : AssistantUpdate;

/// <summary>A tool call started; the overlay shows the pack label while it runs.</summary>
public sealed record AssistantToolStarted(string Name, string Label) : AssistantUpdate;

/// <summary>A tool call finished. <see cref="Summary"/> is a short, already-truncated description.</summary>
public sealed record AssistantToolFinished(string Name, string Label, bool Success, string? Summary) : AssistantUpdate;

/// <summary>A browser follow-up the overlay can offer, e.g. a Play button or a link to an item.</summary>
public sealed record AssistantActionAvailable(AssistantActionLink Link) : AssistantUpdate;

/// <summary>The question finished. <see cref="Cancelled"/> is true when the user stopped it.</summary>
public sealed record AssistantCompleted(
    IReadOnlyList<AssistantToolTrace> Tools,
    bool Cancelled,
    AssistantConversation? Conversation = null) : AssistantUpdate;

/// <summary>One user or assistant line in the popup's short in-memory thread.</summary>
public sealed record AssistantTurn(bool IsUser, string Text);

/// <summary>
/// The bounded context the popup carries between follow-up questions. It is never persisted: only
/// the current overlay holds it, and closing the popup discards it. Recent search results are kept
/// separately so "download the first one" can resolve an id the previous answer only listed.
/// </summary>
public sealed record AssistantConversation(
    IReadOnlyList<AssistantTurn> Turns,
    IReadOnlyList<AssistantMediaSummary> RecentItems)
{
    /// <summary>Longest transcript sent back to the model (user + assistant lines).</summary>
    public const int MaxTurns = 6;

    /// <summary>Longest recent-result list carried into the system prompt.</summary>
    public const int MaxRecentItems = 8;

    public static AssistantConversation Empty { get; } = new([], []);

    /// <summary>
    /// Appends one exchange and trims the transcript to <see cref="MaxTurns"/>. Recent items are
    /// de-duplicated by id, keeping the newest position, then trimmed to the last N.
    /// </summary>
    public static AssistantConversation Append(
        AssistantConversation previous,
        string userText,
        string assistantText,
        IEnumerable<AssistantMediaSummary> recentItems)
    {
        List<AssistantTurn> turns =
        [
            .. previous.Turns,
            new AssistantTurn(IsUser: true, userText),
            new AssistantTurn(IsUser: false, assistantText)
        ];

        if (turns.Count > MaxTurns)
            turns = turns[^MaxTurns..];

        List<AssistantMediaSummary> items = previous.RecentItems
            .Concat(recentItems)
            .Where(i => !string.IsNullOrWhiteSpace(i.Id))
            .GroupBy(i => i.Id, StringComparer.Ordinal)
            .Select(g => g.Last())
            .TakeLast(MaxRecentItems)
            .ToList();

        return new AssistantConversation(turns, items);
    }
}

/// <summary>The question failed before producing an answer (disabled, limited, budget, provider error).</summary>
public sealed record AssistantFailed(string Message) : AssistantUpdate;
