using System.Text.Json;

namespace Arrk.Ai;

/// <summary>
/// Resolves an action to the module-contributed source that owns the media id and applies the
/// cross-cutting safety rules: unknown prefixes fail cleanly, deletion and quality changes are
/// admin-only, and a source that asks for confirmation produces a one-click overlay button as well
/// as a message the model can repeat.
///
/// <para>
/// The runner is shared by the model-facing action pack and by the overlay's confirm button, so a
/// destructive action follows exactly the same path whether it is confirmed in a later chat turn or
/// with the card button.
/// </para>
/// </summary>
public sealed class AssistantActionRunner(IEnumerable<IAssistantMediaActionSource> sources)
{
    readonly IReadOnlyList<IAssistantMediaActionSource> _sources = sources.ToList();

    public bool IsAvailable => _sources.Any(s => s.IsAvailable);

    public bool Supports(AssistantActionKind action) =>
        _sources.Any(s => s.IsAvailable && s.SupportedActions.Contains(action));

    public async Task<AssistantActionOutcome> ExecuteAsync(
        AssistantActionKind action,
        string id,
        string? option,
        bool confirmed,
        AssistantToolContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            return AssistantActionOutcome.Fail("An id from search_media is required.");

        if (action is AssistantActionKind.Delete or AssistantActionKind.Upgrade && !context.IsAdmin)
            return AssistantActionOutcome.Fail("Only an administrator can delete media or change its quality profile.");

        // A quality change is not a deletion, but it can start a large re-download. Require the same
        // explicit click so a prompt injected through a title or release name cannot trigger it.
        if (action == AssistantActionKind.Upgrade && !confirmed)
        {
            const string label = "Change quality profile";
            context.AddUiAction(new AssistantActionLink(
                AssistantActionLinkKind.Confirm,
                label,
                Payload: JsonSerializer.Serialize(new AssistantPendingAction(action, id, option, label))));
            return AssistantActionOutcome.Confirm(
                "Change this item's quality profile and search for an upgrade? Ask the user to confirm.",
                label);
        }

        IAssistantMediaActionSource? source = _sources
            .Where(s => s.IsAvailable && s.SupportedActions.Contains(action))
            .FirstOrDefault(s => id.StartsWith(s.Key + ":", StringComparison.OrdinalIgnoreCase));

        if (source is null)
            return AssistantActionOutcome.Fail($"No available source can {ActionLabel(action)} that item.");

        AssistantActionOutcome outcome;
        try
        {
            outcome = await source.ExecuteAsync(action, id, option, confirmed, context, ct);
        }
        catch (Exception ex)
        {
            return AssistantActionOutcome.Fail($"That action failed: {ex.Message}");
        }

        if (outcome.Link is not null)
            context.AddUiAction(outcome.Link);

        if (outcome.RequiresConfirmation)
        {
            string title = outcome.ConfirmLabel ?? $"{ActionLabel(action)} this item";
            context.AddUiAction(new AssistantActionLink(
                AssistantActionLinkKind.Confirm,
                title,
                Payload: JsonSerializer.Serialize(new AssistantPendingAction(action, id, option, title))));
        }

        return outcome;
    }

    static string ActionLabel(AssistantActionKind action) => action switch
    {
        AssistantActionKind.Request => "request",
        AssistantActionKind.AddToWatchlist => "add to a watchlist",
        AssistantActionKind.RemoveFromWatchlist => "remove from a watchlist",
        AssistantActionKind.Delete => "delete",
        AssistantActionKind.Upgrade => "upgrade",
        AssistantActionKind.Play => "play",
        _ => action.ToString().ToLowerInvariant()
    };
}
