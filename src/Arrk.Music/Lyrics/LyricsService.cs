using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Music.Models;
using Arrk.Music.Services;

namespace Arrk.Music.Lyrics;

/// <summary>Which lyric sidecar formats Arrk may own beside a file.</summary>
static class LyricsSidecar
{
    /// <summary>Sidecar names Navidrome's <c>LyricsPriority</c> understands (its default order).</summary>
    public static readonly string[] Extensions =
        [".lrc", ".elrc", ".ttml", ".yaml", ".yml", ".srt", ".txt"];
}

/// <summary>One track the lyrics sweep may look up, with the metadata a provider query needs.</summary>
public sealed record LyricsTarget(
    int TrackId, string Title, string Artist, string AlbumArtist, string Album, int DurationSeconds, string FilePath);

/// <summary>What happened to the track's lyrics sidecar, so the list's "lyrics" button can act on it.</summary>
public enum LyricsOutcome
{
    Found,
    Instrumental,
    NotFound,

    /// <summary>The provider could not be reached. Not a verdict on the song, it stays eligible.</summary>
    Unavailable,

    /// <summary>A failure on our side (writing the sidecar, the database). The daily reset retries it.</summary>
    Failed
}

/// <summary>The outcome of a lyrics lookup, alongside a message the UI can show.</summary>
public sealed record LyricsRequestResult(LyricsOutcome Outcome, string Message);

/// <summary>
/// Finds lyrics for library tracks and writes them beside the audio files.
///
/// Lyrics are cosmetic, so this never runs on the import path: a background sweep takes a batch of
/// tracks that have no lyrics yet, asks LRCLIB, verifies the match, and writes an <c>.lrc</c> (or
/// <c>.txt</c>) sidecar next to the audio. Writing a sidecar rather than storing text in the database
/// keeps the library self-describing, Navidrome and every Subsonic client pick the lyrics up, and
/// they survive a database rebuild. A miss is not retried on a timer (retrying a song that simply has
/// no lyrics only wastes the provider's goodwill); the daily reset job clears only the transient
/// failures, and a track can always be retried by hand from its menu.
/// </summary>
public sealed class LyricsService(
    IDbContextFactory<ArrkDbContext> dbFactory,
    LrclibClient lrclib,
    IOptionsMonitor<MusicConfig> config,
    IConfiguration configuration,
    LiveActivityRegistry liveActivities,
    MusicNotifications notifications,
    ILogger<LyricsService> logger)
{
    MusicConfig Config => config.CurrentValue;
    LyricsConfig Lyrics => Config.Lyrics;

    string LibraryRoot => MusicPaths.ResolveLibraryRoot(configuration);

    /// <summary>How many consecutive provider failures end a run, at that point LRCLIB is down and
    /// the rest of the batch is better left for the next run than marked failed.</summary>
    const int ConsecutiveFailureLimit = 3;

    /// <summary>
    /// Looks up and writes lyrics for a batch of eligible tracks, least-recently-checked first.
    /// Returns how many now have lyrics. A provider outage stops the run without changing any track's
    /// state, so nothing is parked waiting for the daily reset.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        if (!Lyrics.Enabled)
            return 0;

        List<LyricsTarget> targets = await SelectTargetsAsync(Lyrics.BatchSize, trackId: null, ct);
        if (targets.Count == 0)
            return 0;

        using LiveActivityScope activity = liveActivities.Begin(
            LiveActivityKind.Search, "Searching lyrics", $"0 of {targets.Count}");

        int written = 0;
        int consecutiveFailures = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            LyricsTarget target = targets[i];
            activity.Update(Describe(target, i + 1, targets.Count));

            LyricsOutcome outcome = await LookupAndWriteAsync(target, ct);
            if (outcome == LyricsOutcome.Found)
                written++;

            if (outcome == LyricsOutcome.Unavailable)
            {
                // The provider is having a bad moment; stop the run rather than marching through the
                // batch collecting the same answer. The rest keep their place for the next sweep.
                consecutiveFailures++;
                if (consecutiveFailures >= ConsecutiveFailureLimit)
                {
                    logger.LogWarning(
                        "Stopping the lyrics run after {Count} unavailable responses from LRCLIB", consecutiveFailures);
                    break;
                }
            }
            else
            {
                consecutiveFailures = 0;
            }

            if (i + 1 < targets.Count && Lyrics.RequestDelayMs > 0)
                await Task.Delay(Lyrics.RequestDelayMs, ct);
        }

        if (written > 0)
            logger.LogInformation("Wrote lyrics for {Count} track(s)", written);
        return written;
    }

    /// <summary>
    /// Forces a lyrics lookup for one track, whatever its current state. Backs the song's "Find
    /// lyrics" menu action, so a track with no lyrics, or one whose lookup failed, can be retried
    /// without waiting for the daily reset.
    /// </summary>
    public async Task<LyricsRequestResult> RequestAsync(int trackId, CancellationToken ct = default)
    {
        List<LyricsTarget> targets = await SelectTargetsAsync(1, trackId, ct);
        if (targets.Count == 0)
            return new LyricsRequestResult(LyricsOutcome.NotFound, "That song isn't in the library.");

        LyricsTarget target = targets[0];
        using LiveActivityScope activity = liveActivities.Begin(
            LiveActivityKind.Search, "Searching lyrics", Describe(target, 0, 0));

        LyricsOutcome outcome = await LookupAndWriteAsync(target, ct);
        activity.Update(null);

        return outcome switch
        {
            LyricsOutcome.Found => new(LyricsOutcome.Found, $"Added lyrics for \"{target.Title}\"."),
            LyricsOutcome.Instrumental => new(LyricsOutcome.Instrumental, $"\"{target.Title}\" is instrumental — there are no lyrics."),
            LyricsOutcome.Unavailable => new(LyricsOutcome.Unavailable, $"The lyrics provider is busy right now — try \"{target.Title}\" again in a minute."),
            LyricsOutcome.Failed => new(LyricsOutcome.Failed, $"Couldn't save lyrics for \"{target.Title}\". Try again later."),
            _ => new(LyricsOutcome.NotFound, $"No lyrics found for \"{target.Title}\".")
        };
    }

    /// <summary>
    /// Clears a track's <see cref="LyricsState.Failed"/> state so the next sweep retries it. Called
    /// daily: a failure on our side (writing the sidecar, the database) is worth another attempt,
    /// unlike a track that genuinely has no lyrics, which is never retried.
    /// </summary>
    public async Task<int> ResetFailuresAsync(CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        int cleared = await db.Set<MusicTrack>()
            .Where(t => t.LyricsState == LyricsState.Failed)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LyricsState, LyricsState.None), ct);
        if (cleared > 0)
            logger.LogInformation("Cleared {Count} failed lyrics lookup(s) for a retry", cleared);
        return cleared;
    }

    /// <summary>Reports whether a track already has lyrics (used to label the menu action).</summary>
    public async Task<LyricsState> GetStateAsync(int trackId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Set<MusicTrack>()
            .Where(t => t.Id == trackId)
            .Select(t => t.LyricsState)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The live-activity detail for a track: the song and its artist, with the batch position when
    /// there is one.
    /// </summary>
    static string Describe(LyricsTarget target, int position, int total)
    {
        string who = string.IsNullOrWhiteSpace(target.Artist)
            ? $"\"{target.Title}\""
            : $"\"{target.Title}\" · {target.Artist}";
        return total > 1 ? $"{position} of {total} · {who}" : who;
    }

    // Lookup
    /// <summary>Resolves, verifies and writes one track's lyrics, recording the outcome.</summary>
    async Task<LyricsOutcome> LookupAndWriteAsync(LyricsTarget target, CancellationToken ct)
    {
        await notifications.LyricsSearchStartedAsync(target.Artist, target.Title, ct);
        try
        {
            LyricsCandidate? chosen = await LookupAsync(target, ct);
            if (chosen is null)
            {
                await SetStateAsync(target.TrackId, LyricsState.NotFound, LyricsTier.None, ct);
                await notifications.LyricsNotFoundAsync(target.Artist, target.Title, ct);
                return LyricsOutcome.NotFound;
            }

            LyricsContent? content = LyricsMatcher.ToContent(chosen);
            if (content is null)
            {
                await SetStateAsync(target.TrackId, LyricsState.Instrumental, LyricsTier.None, ct);
                await notifications.LyricsInstrumentalAsync(target.Artist, target.Title, ct);
                return LyricsOutcome.Instrumental;
            }

            WriteSidecar(target, content);
            await SetStateAsync(target.TrackId, LyricsState.Found, content.Tier, ct);
            await notifications.LyricsFoundAsync(target.Artist, target.Album, target.Title, content.Tier, ct);
            return LyricsOutcome.Found;
        }
        catch (LyricsProviderUnavailableException ex)
        {
            // A provider wobble is not a verdict on the song: leave it eligible and let the next sweep
            // (or the user) try again instead of parking it until the daily reset.
            logger.LogDebug(ex, "Lyrics provider unavailable for {Track}", target.Title);
            await TouchAsync(target.TrackId, ct);
            await notifications.LyricsSearchFailedAsync(target.Artist, target.Title, ex.Message, ct);
            return LyricsOutcome.Unavailable;
        }
        catch (Exception ex)
        {
            // A genuine failure of ours (writing the sidecar, the database) is retried by the daily reset.
            logger.LogWarning(ex, "Lyrics lookup failed for {Track}", target.Title);
            await RecordFailureAsync(target, ex.Message, ct);
            return LyricsOutcome.Failed;
        }
    }

    async Task RecordFailureAsync(LyricsTarget target, string reason, CancellationToken ct)
    {
        await SetStateAsync(target.TrackId, LyricsState.Failed, LyricsTier.None, ct);
        await notifications.LyricsSearchFailedAsync(target.Artist, target.Title, reason, ct);
    }

    /// <summary>
    /// Records that we looked, without changing the track's state. Tracks are swept least-recently
    /// checked first, so a song the provider could not answer for rotates behind the queue instead of
    /// being retried forever at the head of every batch.
    /// </summary>
    async Task TouchAsync(int trackId, CancellationToken ct)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        await db.Set<MusicTrack>()
            .Where(t => t.Id == trackId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LyricsCheckedAt, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Finds the best lyrics record for a track.
    ///
    /// The signature lookup comes first (highest precision), then every ranked query accumulates into
    /// one pool: a strong (artist-verified) match ends the search immediately, and only when no query
    /// can produce one is the weaker title+duration tier considered, so a soundtrack credited to its
    /// composer can still resolve without letting a same-titled cover win over the real song.
    ///
    /// A provider failure on one query never abandons the rest; it is remembered, and only surfaced
    /// (as "unavailable", not as "no lyrics") when nothing at all could be resolved.
    /// </summary>
    async Task<LyricsCandidate?> LookupAsync(LyricsTarget target, CancellationToken ct)
    {
        IReadOnlyList<string> artists = LyricsMatcher.DistinctArtists(target.Artist, target.AlbumArtist);
        string clean = MusicText.CleanTrackTitle(target.Title);
        if (string.IsNullOrWhiteSpace(clean))
            clean = target.Title;

        bool providerFailed = false;
        int duration = target.DurationSeconds;

        // Highest precision first: LRCLIB's own signature lookup, without the album (album names drift
        // between releases, and the matcher verifies what comes back anyway).
        foreach (string artist in artists)
        {
            try
            {
                LyricsCandidate? exact = await lrclib.GetAsync(artist, clean, album: null, duration, ct);
                if (exact is not null && LyricsMatcher.PickVerified([exact], artists, target.Title, duration) is not null)
                    return exact;
            }
            catch (LyricsProviderUnavailableException)
            {
                providerFailed = true;
            }
        }

        List<LyricsCandidate> pool = [];
        foreach (LyricsQuery query in LyricsMatcher.BuildQueries(target.Artist, target.AlbumArtist, target.Title))
        {
            try
            {
                List<LyricsCandidate> found = await RunQueryAsync(query, ct);
                if (found.Count > 0)
                    pool.AddRange(found);

                LyricsCandidate? verified = LyricsMatcher.PickVerified(pool, artists, target.Title, duration);
                if (verified is not null)
                    return verified;
            }
            catch (LyricsProviderUnavailableException)
            {
                providerFailed = true;
            }
        }

        LyricsCandidate? byAlbum = LyricsMatcher.PickByAlbumAndDuration(pool, target.Title, target.Album, duration);
        if (byAlbum is not null)
            return byAlbum;

        // Nothing matched. If the provider was failing we cannot call this a miss, the song deserves
        // another attempt rather than a permanent "no lyrics".
        if (providerFailed)
            throw new LyricsProviderUnavailableException($"LRCLIB was unreachable while looking up \"{target.Title}\".");

        return null;
    }

    /// <summary>Runs one planned query, artist-filtered, title-only, or free text.</summary>
    async Task<List<LyricsCandidate>> RunQueryAsync(LyricsQuery query, CancellationToken ct)
    {
        if (query.IsStructured)
            return await lrclib.SearchAsync(query.TrackName, query.ArtistName, query: null, ct);
        if (query.IsTitleOnly)
            return await lrclib.SearchAsync(query.TrackName, artistName: null, query: null, ct);
        return await lrclib.SearchAsync(query: query.Query, ct: ct);
    }

    // Persistence

    async Task<List<LyricsTarget>> SelectTargetsAsync(int take, int? trackId, CancellationToken ct)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);

        IQueryable<MusicTrack> query = db.Set<MusicTrack>().AsNoTracking()
            .Where(t => t.FilePath != "");
        query = trackId is { } id
            ? query.Where(t => t.Id == id)
            // Least-recently-checked first: a song the provider could not answer for drifts behind the
            // rest instead of being retried at the head of every batch.
            : query.Where(t => t.LyricsState == LyricsState.None)
                   .OrderBy(t => t.LyricsCheckedAt ?? DateTime.MinValue)
                   .ThenBy(t => t.Id);

        List<MusicTrack> tracks = await query.Take(take).ToListAsync(ct);
        if (tracks.Count == 0)
            return [];

        int[] albumIds = [.. tracks.Select(t => t.AlbumId).Distinct()];
        Dictionary<int, (string Title, int ArtistId)> albums = await db.Set<MusicAlbum>()
            .Where(a => albumIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Title, a.ArtistId })
            .ToDictionaryAsync(a => a.Id, a => (a.Title, a.ArtistId), ct);

        int[] artistIds = [.. tracks.Select(t => t.ArtistId)
            .Concat(albums.Values.Select(a => a.ArtistId))
            .Distinct()];
        Dictionary<int, string> artists = await db.Set<MusicArtist>()
            .Where(a => artistIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Name })
            .ToDictionaryAsync(a => a.Id, a => a.Name, ct);

        List<LyricsTarget> targets = [];
        foreach (MusicTrack track in tracks)
        {
            (string Title, int ArtistId) album = albums.GetValueOrDefault(track.AlbumId);
            string artist = artists.GetValueOrDefault(track.ArtistId, "");
            string albumArtist = album.ArtistId > 0 && albums.ContainsKey(track.AlbumId)
                ? artists.GetValueOrDefault(album.ArtistId, "")
                : "";
            targets.Add(new LyricsTarget(track.Id, track.Title, artist, albumArtist, album.Title ?? "", track.DurationSeconds, track.FilePath));
        }
        return targets;
    }

    async Task SetStateAsync(int trackId, LyricsState state, LyricsTier tier, CancellationToken ct)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicTrack? track = await db.Set<MusicTrack>().FirstOrDefaultAsync(t => t.Id == trackId, ct);
        if (track is null)
            return;

        track.LyricsState = state;
        track.LyricsTier = tier;
        track.LyricsCheckedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Writes the lyrics beside the audio file under the same stem, clearing any other sidecar format
    /// first so Navidrome never has two lyric files to choose between.
    /// </summary>
    void WriteSidecar(LyricsTarget target, LyricsContent content)
    {
        string full = Path.Combine(LibraryRoot, target.FilePath);
        string directory = Path.GetDirectoryName(full) ?? LibraryRoot;
        Directory.CreateDirectory(directory);
        string stem = Path.GetFileNameWithoutExtension(full);

        foreach (string extension in LyricsSidecar.Extensions)
        {
            string stale = Path.Combine(directory, stem + extension);
            if (File.Exists(stale))
            {
                try { File.Delete(stale); }
                catch (Exception ex) { logger.LogDebug(ex, "Could not remove stale lyrics sidecar {Path}", stale); }
            }
        }

        File.WriteAllText(Path.Combine(directory, stem + content.Extension), content.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
