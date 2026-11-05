using Hangfire;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stackarr.Music.Models;
using Stackarr.Core;
using Stackarr.Core.Services;
using Stackarr.Navidrome;
using Stackarr.Navidrome.Models;

namespace Stackarr.Music.Services;

/// <summary>
/// Apple-Music-style playlist subscriptions: a user subscribes to an external (Deezer) playlist, which
/// is projected into their Navidrome playlists, refreshed on a schedule, and (optionally) has its
/// missing songs downloaded via Stackarr's native acquisition. Owned per user.
/// </summary>
public class MusicSubscriptionService(
    IDbContextFactory<StackarrDbContext> dbContextFactory,
    DeezerService deezer,
    MusicCatalogService catalog,
    NavidromeService navidrome,
    MusicLibraryService library,
    LiveActivityRegistry liveActivities,
    ILogger<MusicSubscriptionService> logger,
    Hangfire.IBackgroundJobClient? backgroundJobs = null)
{
    public const string DeezerSource = "deezer";

    public async Task<List<MusicSubscription>> GetForUserAsync(int userId, CancellationToken ct = default)
    {
        if (userId <= 0) return [];
        try
        {
            await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
            return await db.Set<MusicSubscription>().AsNoTracking()
                .Where(s => s.OwnerUserId == userId)
                .OrderBy(s => s.Name)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not load music subscriptions");
            return [];
        }
    }

    /// <summary>
    /// Removes the Navidrome projections of a user's subscriptions from a playlist list. Subscriptions
    /// render as their own tile and are synced from their source, so they must never appear (or be
    /// edited) as ordinary playlists.
    /// </summary>
    public static List<SubsonicPlaylist> ExcludeSubscribed(
        IEnumerable<SubsonicPlaylist> playlists, IEnumerable<MusicSubscription> subscriptions)
    {
        HashSet<string> subscribedIds = subscriptions
            .Where(s => !string.IsNullOrEmpty(s.NavidromePlaylistId))
            .Select(s => s.NavidromePlaylistId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return playlists.Where(p => !subscribedIds.Contains(p.Id)).ToList();
    }

    public async Task<bool> IsSubscribedAsync(int userId, long sourcePlaylistId, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<MusicSubscription>().AsNoTracking()
            .AnyAsync(s => s.OwnerUserId == userId && s.Source == DeezerSource && s.SourcePlaylistId == sourcePlaylistId, ct);
    }

    /// <summary>
    /// The user's subscription to a source playlist, if any. Lets the Discover popup render a
    /// subscribed Deezer playlist exactly like the library one (matched songs playable, the rest as
    /// previews) instead of an all-preview view.
    /// </summary>
    public async Task<MusicSubscription?> GetForUserBySourceAsync(int userId, long sourcePlaylistId, CancellationToken ct = default)
    {
        if (userId <= 0 || sourcePlaylistId <= 0) return null;
        try
        {
            await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
            return await db.Set<MusicSubscription>().AsNoTracking()
                .FirstOrDefaultAsync(s => s.OwnerUserId == userId && s.Source == DeezerSource
                                          && s.SourcePlaylistId == sourcePlaylistId, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not load the subscription for playlist {PlaylistId}", sourcePlaylistId);
            return null;
        }
    }

    /// <summary>Subscribes to a Deezer playlist and syncs it immediately so it shows up in the library.</summary>
    public async Task<MusicSubscription?> SubscribeAsync(
        int userId, MusicItem playlist, bool autoDownload = true, CancellationToken ct = default)
    {
        if (userId <= 0 || playlist.DeezerId <= 0) return null;

        MusicSubscription sub;
        await using (StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            MusicSubscription? existing = await db.Set<MusicSubscription>()
                .FirstOrDefaultAsync(s => s.OwnerUserId == userId && s.Source == DeezerSource
                                          && s.SourcePlaylistId == playlist.DeezerId, ct);
            if (existing is not null)
            {
                existing.Name = string.IsNullOrWhiteSpace(playlist.Title) ? existing.Name : playlist.Title;
                existing.ArtUrl = playlist.ArtUrl;
                await SyncAsync(existing, ct);
                return existing;
            }

            sub = new MusicSubscription
            {
                OwnerUserId = userId,
                Source = DeezerSource,
                SourcePlaylistId = playlist.DeezerId,
                Name = string.IsNullOrWhiteSpace(playlist.Title) ? "Subscribed playlist" : playlist.Title,
                ArtUrl = playlist.ArtUrl,
                AutoDownload = autoDownload,
                CreatedAt = DateTime.UtcNow
            };
            db.Set<MusicSubscription>().Add(sub);
            await db.SaveChangesAsync(ct);
        }

        await SyncAsync(sub, ct);
        return sub;
    }

    public async Task<bool> UnsubscribeAsync(int userId, bool isAdmin, int id, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        MusicSubscription? sub = await db.Set<MusicSubscription>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (sub is null || (sub.OwnerUserId != userId && !isAdmin))
            return false;

        if (!string.IsNullOrEmpty(sub.NavidromePlaylistId))
        {
            try { await library.DeletePlaylistAsync(sub.OwnerUserId, true, sub.NavidromePlaylistId, ct); }
            catch (Exception ex) { logger.LogDebug(ex, "Could not delete mirrored playlist for subscription {Id}", id); }
        }

        db.Set<MusicSubscription>().Remove(sub);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Refreshes every subscription (called by the scheduled sync job).</summary>
    public async Task<int> SyncAllAsync(CancellationToken ct = default)
    {
        List<MusicSubscription> subs;
        await using (StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            subs = await db.Set<MusicSubscription>().AsNoTracking().ToListAsync(ct);
        }

        foreach (MusicSubscription sub in subs)
        {
            if (ct.IsCancellationRequested) break;
            await SyncAsync(sub, ct);
        }
        return subs.Count;
    }

    /// <summary>Pulls the source playlist, matches it, queues the missing songs and re-projects it.</summary>
    public async Task SyncAsync(MusicSubscription sub, CancellationToken ct = default)
    {
        using LiveActivityScope activity = liveActivities.Begin(
            LiveActivityKind.Sync, $"Syncing playlist \"{sub.Name}\"", "Fetching tracks from Deezer");
        try
        {
            List<MusicItem> tracks = await deezer.GetPlaylistTracksAsync(sub.SourcePlaylistId, 100, ct);

            // Match each track to a Navidrome song (bounded concurrency to be gentle on Navidrome).
            activity.Update($"Matching {tracks.Count} track(s) on Navidrome");
            string[] found = new string[tracks.Count];
            using (SemaphoreSlim gate = new(6))
            {
                await Task.WhenAll(tracks.Select(async (track, index) =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        // Deezer's playlist titles carry edition tails ("Invisible Touch (Remastered
                        // 2007)") that the file or Navidrome tag does not, or spells them differently
                        // from the album ("(2007 Remaster)"). Match on the clean title, otherwise a song
                        // we already hold stays missing and is re-queued on every sync.
                        string searchTitle = MusicText.CleanTrackTitle(track.Title);
                        if (string.IsNullOrWhiteSpace(searchTitle))
                            searchTitle = track.Title;
                        found[index] = await navidrome.FindSongIdAsync(searchTitle, track.ArtistName, ct) ?? "";
                    }
                    finally { gate.Release(); }
                }));
            }
            List<string> songIds = [.. found.Where(id => !string.IsNullOrEmpty(id)).Distinct()];
            activity.Update($"{songIds.Count} of {tracks.Count} track(s) in your library");

            string? navId = await library.UpsertProjectedPlaylistAsync(
                sub.OwnerUserId, sub.NavidromePlaylistId, sub.Name, songIds, ct);

            // Carry each song's last acquisition attempt over from the previous sync, so a re-sync
            // (which now fires after every import) can tell "already tried recently" from "never
            // attempted" and doesn't re-queue the same hard songs forever. See EnqueueMissingGrabs.
            Dictionary<string, DateTime> attempts = GetTracks(sub)
                .Where(t => t.AcquireAttemptedAt is not null)
                .GroupBy(t => TrackKey(t.Artist, t.Title), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Max(t => t.AcquireAttemptedAt!.Value), StringComparer.Ordinal);

            sub.NavidromePlaylistId = navId ?? sub.NavidromePlaylistId;
            sub.TrackCount = tracks.Count;
            sub.MatchCount = songIds.Count;
            sub.LastSyncedAt = DateTime.UtcNow;
            sub.LastError = "";
            sub.TracksJson = JsonSerializer.Serialize(tracks.Select((t, i) => new MusicSubscriptionTrack(
                t.Title, t.ArtistName, string.IsNullOrEmpty(t.AlbumName) ? "" : t.AlbumName, t.ArtUrl,
                found[i], t.PreviewUrl, t.DeezerId,
                attempts.GetValueOrDefault(TrackKey(t.ArtistName, t.Title)))));

            // Auto-fill: enqueue a track-level grab for every song we don't have yet. These run in the
            // background (Soulseek-first for tracks), so the sync itself stays fast and the playlist
            // fills in as downloads land (the importer re-syncs it).
            if (sub.AutoDownload)
                await EnqueueMissingGrabsAsync(sub, ct);
        }
        catch (Exception ex)
        {
            sub.LastError = ex.Message;
            logger.LogDebug(ex, "Music subscription {Id} sync failed", sub.Id);
        }

        try
        {
            await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
            db.Set<MusicSubscription>().Update(sub);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not persist music subscription {Id}", sub.Id);
        }
    }

    /// <summary>
    /// Adds the subscription's missing songs to the acquisition queue, at most
    /// <see cref="MaxGrabsPerSync"/> per sync. A song attempted within <see cref="GrabRetryCooldown"/>
    /// is skipped, so each re-sync takes the next batch of unattempted songs. Idempotent, because the
    /// queue dedupes by request.
    /// </summary>
    async Task<int> EnqueueMissingGrabsAsync(MusicSubscription sub, CancellationToken ct)
    {
        List<MusicSubscriptionTrack> tracks = GetTracks(sub);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        int queued = 0;
        foreach (MusicSubscriptionTrack track in tracks)
        {
            if (track.InLibrary || string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title))
                continue;
            if (!seen.Add(TrackKey(track.Artist, track.Title)))
                continue;

            await catalog.EnqueueTrackAcquireAsync(track.Artist, track.Title, track.DeezerTrackId, albumId: 0, ct);
            queued++;
        }

        if (queued > 0)
        {
            catalog.KickAcquireQueue();
            logger.LogInformation("Queued {Count} missing song(s) for subscription \"{Name}\"", queued, sub.Name);
        }
        return queued;
    }

    /// <summary>Stable per-song key (normalized artist + title) used to carry state across syncs.</summary>
    static string TrackKey(string artist, string title) =>
        $"{Normalize(artist)}\u0001{Normalize(title)}";

    static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The source playlist's tracks (some may not be in the library yet, see <see cref="MusicSubscriptionTrack.InLibrary"/>).</summary>
    public List<MusicSubscriptionTrack> GetTracks(MusicSubscription sub)
    {
        if (string.IsNullOrWhiteSpace(sub.TracksJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<MusicSubscriptionTrack>>(sub.TracksJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Pulls the latest from the source (a re-sync), then queues a grab for every song still missing;
    /// the playlist "Refresh" action.
    /// </summary>
    public async Task<int> RefreshAsync(MusicSubscription sub, CancellationToken ct = default)
    {
        await SyncAsync(sub, ct);
        return await EnqueueMissingGrabsAsync(sub, ct);
    }

    /// <summary>Adds the subscription's missing songs to the acquisition queue (the "search missing" action).</summary>
    public Task<int> SearchMissingAsync(MusicSubscription sub, CancellationToken ct = default) =>
        EnqueueMissingGrabsAsync(sub, ct);
}

/// <summary>A single track expected by a playlist subscription, and whether it is present in the library.</summary>
public record MusicSubscriptionTrack(
    string Title,
    string Artist,
    string Album,
    string CoverArtUrl,
    string SongId,
    string PreviewUrl = "",
    long DeezerTrackId = 0,
    DateTime? AcquireAttemptedAt = null)
{
    public bool InLibrary => !string.IsNullOrEmpty(SongId);
}
