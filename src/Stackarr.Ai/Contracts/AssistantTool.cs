using Microsoft.Extensions.AI;

namespace Stackarr.Ai;

/// <summary>Who may use a tool pack.</summary>
public enum AssistantAccess
{
    /// <summary>Any signed-in user; tools must scope their answers to that user.</summary>
    User,

    /// <summary>Administrators only; the runtime filters the pack out for everyone else.</summary>
    Admin
}

/// <summary>
/// Everything a tool needs to answer one request: the caller (for permissions and per-user data), a
/// per-turn service scope for scoped services, and the request cancellation token. Tools receive this
/// when the turn starts and must not capture it beyond the call.
/// </summary>
public sealed record AssistantToolContext(
    int UserId,
    string Username,
    bool IsAdmin,
    IServiceProvider Services,
    CancellationToken CancellationToken,
    IList<AssistantActionLink>? UiActions = null,
    IList<AssistantMediaSummary>? ContextItems = null)
{
    public bool CanAccess(AssistantAccess access) => access == AssistantAccess.User || IsAdmin;

    /// <summary>Adds a browser follow-up (play/open) the overlay renders under the answer.</summary>
    public void AddUiAction(AssistantActionLink link) => UiActions?.Add(link);

    /// <summary>
    /// Remembers items a tool returned so a follow-up ("download the first one") can resolve an id
    /// without searching again. The list is bounded by the runtime.
    /// </summary>
    public void AddContextItems(IEnumerable<AssistantMediaSummary> items)
    {
        if (ContextItems is null)
            return;

        foreach (AssistantMediaSummary item in items)
            ContextItems.Add(item);
    }
}

/// <summary>
/// One pack of assistant tools contributed by a module. A pack owns the functions for the data it
/// owns; the runtime (in <c>Stackarr.Ai</c>) never references a domain module.
///
/// <para>
/// <see cref="Keywords"/> is the cheap router: an empty list means the pack is always offered,
/// otherwise the pack is offered only when the user's message mentions one of them. This keeps the
/// tool schema small without spending a second model call on routing.
/// </para>
/// </summary>
public interface IAssistantTool
{
    /// <summary>Stable key for logging/tests, e.g. <c>library</c>.</summary>
    string Key { get; }

    string Name { get; }

    string Description { get; }

    AssistantAccess Access { get; }

    /// <summary>Lower-case words that make this pack relevant. Empty = always offered.</summary>
    IReadOnlyList<string> Keywords { get; }

    /// <summary>Live availability (master switch, owning service's config, connection state).</summary>
    bool IsAvailable { get; }

    /// <summary>Builds the pack's tools bound to this request's scope.</summary>
    IReadOnlyList<AITool> CreateTools(AssistantToolContext context);
}

/// <summary>What one tool invocation produced, kept on the message for the UI and the model history.</summary>
public sealed record AssistantToolTrace(string Name, string Label, bool Success, string? Summary = null);
