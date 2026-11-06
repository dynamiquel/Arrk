namespace Arrk.Ai;

/// <summary>An action the assistant may perform on one media item.</summary>
public enum AssistantActionKind
{
    Request,
    AddToWatchlist,
    RemoveFromWatchlist,
    Delete,
    Upgrade,
    Play
}

/// <summary>How the assistant overlay should render a returned link.</summary>
public enum AssistantActionLinkKind
{
    /// <summary>Open an Arrk or Jellyfin page in the browser.</summary>
    Open,

    /// <summary>Start the in-site music player with a serialized queue payload.</summary>
    MusicPlay,

    /// <summary>Confirm a destructive action without another model turn; payload is a pending action.</summary>
    Confirm
}

/// <summary>
/// The action an overlay confirmation button will run directly. It is serialized into
/// <see cref="AssistantActionLink.Payload"/> so the one-shot popup does not need chat history.
/// </summary>
public sealed record AssistantPendingAction(
    AssistantActionKind Action,
    string Id,
    string? Option,
    string Title);

/// <summary>
/// A follow-up the overlay can offer after an action. Actions that need the browser (play, open a
/// detail page) return this instead of pretending the server clicked something for the user.
/// </summary>
public sealed record AssistantActionLink(
    AssistantActionLinkKind Kind,
    string Label,
    string Url = "",
    string? Payload = null);

/// <summary>What one action invocation did, in terms the model can quote back.</summary>
public sealed record AssistantActionOutcome(
    bool Success,
    string Message,
    bool RequiresConfirmation = false,
    AssistantActionLink? Link = null,
    string? ConfirmLabel = null)
{
    public static AssistantActionOutcome Ok(string message, AssistantActionLink? link = null) =>
        new(true, message, false, link);

    public static AssistantActionOutcome Fail(string message) =>
        new(false, message);

    public static AssistantActionOutcome Confirm(string message, string? confirmLabel = null) =>
        new(false, message, RequiresConfirmation: true, ConfirmLabel: confirmLabel);
}

/// <summary>
/// Action capability contributed by a module for the ids its media source produces. The assistant
/// runtime never references a domain module: it routes by the id prefix to the first source that
/// claims it. Implementations enforce caller permissions and confirmation themselves; the tool is
/// the thin, model-facing wrapper.
/// </summary>
public interface IAssistantMediaActionSource
{
    /// <summary>Same key as the matching <see cref="IAssistantMediaSource"/> (id prefix).</summary>
    string Key { get; }

    bool IsAvailable { get; }

    /// <summary>Actions this source can perform. Destructive actions still require confirmation.</summary>
    IReadOnlyList<AssistantActionKind> SupportedActions { get; }

    Task<AssistantActionOutcome> ExecuteAsync(
        AssistantActionKind action,
        string id,
        string? option,
        bool confirmed,
        AssistantToolContext context,
        CancellationToken ct);
}
