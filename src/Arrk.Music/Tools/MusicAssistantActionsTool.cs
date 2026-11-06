using System.Text.Json;
using Microsoft.Extensions.AI;
using Arrk.Ai;

namespace Arrk.Music.Tools;

/// <summary>
/// The music action pack, owned by the music module. It is bound to a runner containing only
/// <see cref="Services.MusicAssistantActionSource"/>, so a music tool can never act on a TV id.
/// </summary>
public sealed class MusicAssistantActionsTool(AssistantActionRunner actions) : IAssistantTool
{
    public string Key => "music-actions";
    public string Name => "Music actions";
    public string Description => "Request albums/artists, manage their watchlist, delete them and play them.";
    public AssistantAccess Access => AssistantAccess.User;

    public IReadOnlyList<string> Keywords =>
    [
        "request", "add", "delete", "remove", "play", "download", "watchlist", "grab", "get me",
        "want", "put ", "start", "listen", "fetch", "acquire", "album", "artist", "song", "track"
    ];

    public bool IsAvailable => actions.IsAvailable;

    public IReadOnlyList<AITool> CreateTools(AssistantToolContext context)
    {
        return
        [
            AIFunctionFactory.Create(
                (string id, string? quality = null, CancellationToken ct = default) =>
                    ExecuteAsync(AssistantActionKind.Request, id, quality, false, context, ct),
                "music_request_media",
                "Requests an album or artist the user does not have. Only accepts ids starting with music: " +
                "(from search_media). Requests may be auto-approved or wait for an administrator."),

            AIFunctionFactory.Create(
                (string id, string? list = null, CancellationToken ct = default) =>
                    ExecuteAsync(AssistantActionKind.AddToWatchlist, id, list, false, context, ct),
                "music_add_to_watchlist",
                "Adds an album or artist (music: id) to the user's watchlist."),

            AIFunctionFactory.Create(
                (string id, string? list = null, CancellationToken ct = default) =>
                    ExecuteAsync(AssistantActionKind.RemoveFromWatchlist, id, list, false, context, ct),
                "music_remove_from_watchlist",
                "Removes an album or artist (music: id) from the user's watchlist."),

            AIFunctionFactory.Create(
                (string id, CancellationToken ct = default) =>
                    ExecuteAsync(AssistantActionKind.Delete, id, null, confirmed: false, context, ct),
                "music_delete_media",
                "Deletes a downloaded album, artist or track (music: id) and its files. Administrator only. " +
                "This never deletes directly: it returns a confirmation button the user must press."),

            AIFunctionFactory.Create(
                (string id, CancellationToken ct = default) =>
                    ExecuteAsync(AssistantActionKind.Play, id, null, false, context, ct),
                "music_play_media",
                "Starts playback for a downloaded album, artist or track (music: id). Returns a Play button " +
                "because the browser, not the server, starts playback.")
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
