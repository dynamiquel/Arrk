using System.Text.Json;
using Microsoft.Extensions.AI;
using Arrk.Ai;

namespace Arrk.Tvarr.Tools;

/// <summary>
/// The TV/movie action pack, owned by the TV module. It is bound to a runner containing only
/// <see cref="Services.TvarrAssistantActionSource"/>, so it can never reach a music or plugin action.
/// Tool names carry the <c>tv_</c> prefix to keep them unique across module packs.
/// </summary>
public sealed class TvarrAssistantActionsTool(AssistantActionRunner actions) : IAssistantTool
{
    public string Key => "tv-actions";
    public string Name => "Movies & TV actions";
    public string Description => "Request movies/series, manage their watchlist, delete, upgrade quality and play them.";
    public AssistantAccess Access => AssistantAccess.User;

    public IReadOnlyList<string> Keywords =>
    [
        "request", "add", "delete", "remove", "upgrade", "play", "download", "watchlist", "grab",
        "get me", "want", "put ", "start", "watch", "fetch", "acquire", "4k", "uhd", "queue"
    ];

    public bool IsAvailable => actions.IsAvailable;

    public IReadOnlyList<AITool> CreateTools(AssistantToolContext context)
    {
        return
        [
            AIFunctionFactory.Create(
                (string id, string? quality = null, CancellationToken ct = default) =>
                    ExecuteAsync(AssistantActionKind.Request, id, quality, false, context, ct),
                "tv_request_media",
                "Requests a movie, series, season or episode the user does not have. Only accepts ids starting with tv: " +
                "(from search_media). Requests may be auto-approved or wait for an administrator."),

            AIFunctionFactory.Create(
                (string id, string? list = null, CancellationToken ct = default) =>
                    ExecuteAsync(AssistantActionKind.AddToWatchlist, id, list, false, context, ct),
                "tv_add_to_watchlist",
                "Adds a movie or series (tv: id) to the user's watchlist."),

            AIFunctionFactory.Create(
                (string id, string? list = null, CancellationToken ct = default) =>
                    ExecuteAsync(AssistantActionKind.RemoveFromWatchlist, id, list, false, context, ct),
                "tv_remove_from_watchlist",
                "Removes a movie or series (tv: id) from the user's watchlist."),

            AIFunctionFactory.Create(
                (string id, CancellationToken ct = default) =>
                    ExecuteAsync(AssistantActionKind.Delete, id, null, confirmed: false, context, ct),
                "tv_delete_media",
                "Deletes a movie, series or season (tv: id) and its files. Administrator only. " +
                "This never deletes directly: it returns a confirmation button the user must press."),

            AIFunctionFactory.Create(
                (string id, string? target = null, CancellationToken ct = default) =>
                    ExecuteAsync(AssistantActionKind.Upgrade, id, target, false, context, ct),
                "tv_upgrade_quality",
                "Switches a movie or series (tv: id) to a higher quality profile (default UHD/4K) and searches for an upgrade. Administrator only."),

            AIFunctionFactory.Create(
                (string id, CancellationToken ct = default) =>
                    ExecuteAsync(AssistantActionKind.Play, id, null, false, context, ct),
                "tv_play_media",
                "Starts playback for a downloaded movie, series or episode (tv: id). Returns a Play button because " +
                "the browser, not the server, starts playback.")
        ];
    }

    async Task<JsonElement> ExecuteAsync(
        AssistantActionKind action,
        string id,
        string? option,
        bool confirmed,
        AssistantToolContext context,
        CancellationToken ct)
    {
        AssistantActionOutcome outcome = await actions.ExecuteAsync(action, id, option, confirmed, context, ct);
        return Json(new
        {
            ok = outcome.Success,
            requiresConfirmation = outcome.RequiresConfirmation,
            message = outcome.Message
        });
    }

    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}
