using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Arrk.Ai;
using Arrk.Mal.Models;
using Arrk.Mal.Services;

namespace Arrk.Mal.Tools;

/// <summary>
/// MyAnimeList list and progress updates for the signed-in user. Reads come from Arrk's cache
/// (refreshed on demand); writes go through <see cref="MalListService"/>, which talks to MAL and is
/// the same path the MAL page uses. Titles are resolved against the user's own list first, and an
/// ambiguous match returns candidates instead of guessing.
/// </summary>
public sealed class MalAssistantTool(
    IOptionsMonitor<MalConfig> config) : IAssistantTool
{
    public string Key => "mal";
    public string Name => "MyAnimeList";
    public string Description => "Read the user's MyAnimeList entries and update watch status, score or watched episodes.";
    public AssistantAccess Access => AssistantAccess.User;

    public IReadOnlyList<string> Keywords =>
    [
        "myanimelist", "my anime list", "mal ", "mal list", "anime list", "watch state", "watch status",
        "mark watched", "mark as watched", "episode", "episodes watched", "score", "rate ", "rating",
        "plan to watch", "on hold", "dropped", "completed", "watching"
    ];

    public bool IsAvailable => config.CurrentValue.IsConfigured;

    public IReadOnlyList<AITool> CreateTools(AssistantToolContext context)
    {
        return
        [
            AIFunctionFactory.Create(
                async (string? status = null, string? search = null, int limit = 25, CancellationToken ct = default) =>
                    await ListAsync(context, status, search, limit, ct),
                "mal_list",
                "Lists the user's MyAnimeList entries with malId, title, status, score, watched/total episodes. " +
                "Optional status (watching|completed|on_hold|dropped|plan_to_watch) and title search."),

            AIFunctionFactory.Create(
                async (int? malId = null, string? title = null, string? status = null, int? score = null,
                    int? watchedEpisodes = null, CancellationToken ct = default) =>
                    await UpdateAsync(context, malId, title, status, score, watchedEpisodes, ct),
                "mal_update",
                "Updates one MyAnimeList entry: status, score (0-10) and/or watched episodes. Resolve a title " +
                "first with mal_list if unsure; an ambiguous title returns candidates with malIds. Pass malId " +
                "from that list to update a specific entry.")
        ];
    }

    async Task<JsonElement> ListAsync(
        AssistantToolContext context, string? status, string? search, int limit, CancellationToken ct)
    {
        MalListService? lists = context.Services.GetService<MalListService>();
        MalAccountService? accounts = context.Services.GetService<MalAccountService>();
        if (lists is null || accounts is null)
            return Json(new { available = false, message = "The MyAnimeList integration is not available." });

        MalAccount? account = await accounts.GetAsync(context.UserId, ct);
        if (account is null)
            return Json(new { available = false, message = "Link MyAnimeList from your account settings first." });
        if (account.NeedsReauth)
            return Json(new { available = false, message = "MyAnimeList needs to be reconnected from your account settings." });

        try
        {
            await lists.EnsureFreshAsync(context.UserId, TimeSpan.FromMinutes(10), ct);
        }
        catch
        {
            // A refresh failure must not hide the cached list.
        }

        string? mappedStatus = MapStatus(status);
        IReadOnlyList<MalEntryView> entries = await lists.GetEntriesAsync(context.UserId, mappedStatus, search, ct);
        int take = Math.Clamp(limit <= 0 ? 25 : limit, 1, 50);

        List<object> items = entries.Take(take).Select(e => (object)new
        {
            malId = e.Entry.MalId,
            title = e.DisplayTitle,
            englishTitle = e.Catalog.EnglishTitle,
            mediaType = e.Catalog.MediaType,
            status = e.Entry.Status,
            statusLabel = e.StatusLabel,
            score = e.Entry.Score,
            watchedEpisodes = e.Entry.WatchedEpisodes,
            totalEpisodes = e.Catalog.NumEpisodes,
            isRewatching = e.Entry.IsRewatching,
            tags = e.Entry.Tags,
            poster = e.Poster
        }).ToList();

        return Json(new
        {
            available = true,
            count = items.Count,
            user = account.MalUsername,
            items
        });
    }

    async Task<JsonElement> UpdateAsync(
        AssistantToolContext context,
        int? malId,
        string? title,
        string? status,
        int? score,
        int? watchedEpisodes,
        CancellationToken ct)
    {
        if (status is null && score is null && watchedEpisodes is null)
            return Json(new { ok = false, message = "Tell me what to change: status, score or watched episodes." });
        if (score is < 0 or > 10)
            return Json(new { ok = false, message = "A MyAnimeList score must be between 0 and 10." });
        if (watchedEpisodes is < 0)
            return Json(new { ok = false, message = "Watched episodes cannot be negative." });

        string? mappedStatus = MapStatus(status);
        if (!string.IsNullOrWhiteSpace(status) && mappedStatus is null)
            return Json(new { ok = false, message = "Status must be watching, completed, on_hold, dropped or plan_to_watch." });

        MalListService? lists = context.Services.GetService<MalListService>();
        MalAccountService? accounts = context.Services.GetService<MalAccountService>();
        if (lists is null || accounts is null)
            return Json(new { ok = false, message = "The MyAnimeList integration is not available." });

        MalAccount? account = await accounts.GetAsync(context.UserId, ct);
        if (account is null)
            return Json(new { ok = false, message = "Link MyAnimeList from your account settings first." });
        if (account.NeedsReauth)
            return Json(new { ok = false, message = "MyAnimeList needs to be reconnected from your account settings." });

        int targetId = malId ?? 0;
        if (targetId <= 0)
        {
            if (string.IsNullOrWhiteSpace(title))
                return Json(new { ok = false, message = "Give me an anime title or a malId." });

            try
            {
                await lists.EnsureFreshAsync(context.UserId, TimeSpan.FromMinutes(10), ct);
            }
            catch
            {
                // Fall back to the cached list.
            }

            ResolvedMal resolved = await ResolveAsync(lists, context.UserId, title, ct);
            if (resolved.MalId <= 0)
            {
                return Json(new
                {
                    ok = false,
                    ambiguous = resolved.Candidates.Count > 1,
                    message = resolved.Candidates.Count > 0
                        ? "I found more than one MyAnimeList entry. Ask which one, or pass its malId."
                        : $"I could not find '{title}' on your MyAnimeList.",
                    candidates = resolved.Candidates
                });
            }

            targetId = resolved.MalId;
        }

        try
        {
            MalEntryView? updated = await lists.UpdateAsync(
                context.UserId, targetId,
                new MalListUpdate(
                    Status: mappedStatus,
                    Score: score,
                    WatchedEpisodes: watchedEpisodes),
                ct);

            if (updated is null)
                return Json(new { ok = false, message = "MyAnimeList did not return the updated entry." });

            return Json(new
            {
                ok = true,
                malId = updated.Entry.MalId,
                title = updated.DisplayTitle,
                status = updated.Entry.Status,
                statusLabel = updated.StatusLabel,
                score = updated.Entry.Score,
                watchedEpisodes = updated.Entry.WatchedEpisodes,
                totalEpisodes = updated.Catalog.NumEpisodes,
                message = $"Updated {updated.DisplayTitle}: {updated.StatusLabel}, " +
                          $"{updated.Entry.WatchedEpisodes}/{updated.Catalog.NumEpisodes} episodes, score {updated.Entry.Score}."
            });
        }
        catch (InvalidOperationException ex)
        {
            return Json(new { ok = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, message = $"MyAnimeList rejected that update: {ex.Message}" });
        }
    }

    static async Task<ResolvedMal> ResolveAsync(
        MalListService lists, int userId, string title, CancellationToken ct)
    {
        IReadOnlyList<MalEntryView> local = await lists.GetEntriesAsync(userId, search: title, ct: ct);
        List<MalCandidate> ranked = local
            .Select(e => new MalCandidate(
                e.Entry.MalId,
                e.DisplayTitle,
                e.Entry.Status,
                e.Entry.WatchedEpisodes,
                e.Catalog.NumEpisodes,
                TitleScore(title, e)))
            .Where(c => c.MatchScore > 0)
            .OrderByDescending(c => c.MatchScore)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ranked.Count > 0)
        {
            int top = ranked[0].MatchScore;
            List<MalCandidate> best = ranked.Where(c => c.MatchScore == top).ToList();
            return best.Count == 1
                ? new ResolvedMal(best[0].MalId, [])
                : new ResolvedMal(0, best.Take(6).ToList());
        }

        // Not on the user's list: search the MAL catalogue so the model can offer candidates. Do not
        // add or update anything yet.
        try
        {
            IReadOnlyList<MalCatalogEntry> catalog = await lists.SearchAsync(userId, title, ct);
            List<MalCandidate> candidates = catalog
                .Select(c => new MalCandidate(
                    c.MalId, c.DisplayTitle, "", 0, c.NumEpisodes,
                    MalTitleMatcher.Score(title, c.EnglishTitle, c.Title, c.DisplayTitle, c.JapaneseTitle, c.Synonyms)))
                .Where(c => c.MatchScore > 0)
                .OrderByDescending(c => c.MatchScore)
                .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();

            return new ResolvedMal(0, candidates);
        }
        catch
        {
            return new ResolvedMal(0, []);
        }
    }

    static int TitleScore(string wanted, MalEntryView entry) =>
        MalTitleMatcher.Score(wanted, entry.Catalog.EnglishTitle, entry.Catalog.Title, entry.DisplayTitle,
            entry.Catalog.JapaneseTitle, entry.Catalog.Synonyms);

    static string? MapStatus(string? status) => status?.Trim().ToLowerInvariant().Replace("-", "_").Replace(' ', '_') switch
    {
        null or "" => null,
        "watching" or "watch" or "current" => MalStatuses.Watching,
        "completed" or "complete" or "finished" => MalStatuses.Completed,
        "on_hold" or "hold" or "paused" => MalStatuses.OnHold,
        "dropped" or "drop" => MalStatuses.Dropped,
        "plan_to_watch" or "plan" or "planned" or "ptw" => MalStatuses.PlanToWatch,
        _ => null
    };

    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    readonly record struct ResolvedMal(int MalId, IReadOnlyList<MalCandidate> Candidates);

    sealed record MalCandidate(
        int MalId, string Title, string Status, int WatchedEpisodes, int TotalEpisodes, int MatchScore);
}
