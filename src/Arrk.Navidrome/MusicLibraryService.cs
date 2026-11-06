using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Arrk.Core;
using Arrk.Navidrome.Models;

namespace Arrk.Navidrome;

/// <summary>
/// Per-user music personalisation: which playlists an Arrk account owns and what each user has
/// listened to. Navidrome itself is driven by a single shared API account, so this layer is what
/// makes playlists and "Recently Played" history differ between users in the Arrk UI.
/// </summary>
public class MusicLibraryService(
    IDbContextFactory<ArrkDbContext> dbContextFactory,
    NavidromeService navidrome,
    ILogger<MusicLibraryService> logger)
{
    /// <summary>
    /// Playlists visible to a user: the ones they created, plus unowned playlists for admins.
    /// </summary>
    public async Task<List<SubsonicPlaylist>> GetPlaylistsForUserAsync(int userId, bool isAdmin, CancellationToken ct = default)
    {
        List<SubsonicPlaylist> all = await navidrome.GetPlaylistsAsync(ct);
        if (all.Count == 0) return all;

        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        Dictionary<string, int> owners = await db.Set<MusicPlaylistOwner>().AsNoTracking()
            .ToDictionaryAsync(o => o.PlaylistId, o => o.OwnerUserId, ct);

        return all.Where(p => owners.TryGetValue(p.Id, out int owner) ? owner == userId : isAdmin).ToList();
    }

    public async Task<bool> CanAccessAsync(int userId, bool isAdmin, string playlistId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        MusicPlaylistOwner? owner = await db.Set<MusicPlaylistOwner>().AsNoTracking()
            .FirstOrDefaultAsync(o => o.PlaylistId == playlistId, ct);
        return owner is null ? isAdmin : owner.OwnerUserId == userId;
    }

    /// <summary>Creates a Navidrome playlist and records the caller as its owner.</summary>
    public async Task<string?> CreatePlaylistAsync(int userId, string name, IEnumerable<string> songIds, CancellationToken ct = default)
    {
        string? id = await navidrome.CreatePlaylistAsync(name, songIds, ct);
        if (string.IsNullOrEmpty(id))
        {
            logger.LogWarning("Created playlist '{Name}' but Navidrome returned no id; ownership not recorded", name);
            return id;
        }

        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        db.Set<MusicPlaylistOwner>().Add(new MusicPlaylistOwner
        {
            PlaylistId = id,
            OwnerUserId = userId,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return id;
    }

    public async Task<bool> DeletePlaylistAsync(int userId, bool isAdmin, string playlistId, CancellationToken ct = default)
    {
        if (!await CanAccessAsync(userId, isAdmin, playlistId, ct)) return false;
        await navidrome.DeletePlaylistAsync(playlistId, ct);
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        await db.Set<MusicPlaylistOwner>().Where(o => o.PlaylistId == playlistId).ExecuteDeleteAsync(ct);
        return true;
    }

    public async Task<bool> RenamePlaylistAsync(int userId, bool isAdmin, string playlistId, string name, CancellationToken ct = default)
    {
        if (!await CanAccessAsync(userId, isAdmin, playlistId, ct)) return false;
        await navidrome.RenamePlaylistAsync(playlistId, name, ct);
        return true;
    }

    /// <summary>Adopts unowned playlists for a user once, so playlists created before per-user ownership survive.</summary>
    public async Task<int> ClaimUnownedPlaylistsAsync(int userId, CancellationToken ct = default)
    {
        List<SubsonicPlaylist> all = await navidrome.GetPlaylistsAsync(ct);
        if (all.Count == 0) return 0;

        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        HashSet<string> owned = (await db.Set<MusicPlaylistOwner>().AsNoTracking().Select(o => o.PlaylistId).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        List<MusicPlaylistOwner> adopt = all
            .Where(p => !owned.Contains(p.Id))
            .Select(p => new MusicPlaylistOwner { PlaylistId = p.Id, OwnerUserId = userId, CreatedAt = DateTime.UtcNow })
            .ToList();

        if (adopt.Count > 0)
        {
            db.Set<MusicPlaylistOwner>().AddRange(adopt);
            await db.SaveChangesAsync(ct);
        }
        return adopt.Count;
    }

    /// <summary>
    /// Creates or replaces a playlist projected from a subscription: writes the Navidrome tracklist
    /// and records/keeps the user's ownership of it.
    /// </summary>
    public async Task<string?> UpsertProjectedPlaylistAsync(
        int userId, string? playlistId, string name, IReadOnlyList<string> songIds, CancellationToken ct = default)
    {
        string? id = null;
        if (!string.IsNullOrEmpty(playlistId))
            id = await navidrome.CreateOrReplacePlaylistAsync(playlistId, name, songIds, ct);
        // A stale id (playlist deleted in Navidrome) fails; recreate it so the subscription heals.
        if (string.IsNullOrEmpty(id))
            id = await navidrome.CreateOrReplacePlaylistAsync(null, name, songIds, ct);
        if (string.IsNullOrEmpty(id)) return id;

        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        if (!await db.Set<MusicPlaylistOwner>().AnyAsync(o => o.PlaylistId == id, ct))
        {
            db.Set<MusicPlaylistOwner>().Add(new MusicPlaylistOwner
            {
                PlaylistId = id,
                OwnerUserId = userId,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }
        return id;
    }

    // Listening history

    public async Task RecordPlayAsync(
        int userId, string songId, string title, string artist, string album, string coverArtUrl,
        CancellationToken ct = default)
    {
        if (userId <= 0 || string.IsNullOrEmpty(songId)) return;
        try
        {
            await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
            db.Set<MusicPlayHistory>().Add(new MusicPlayHistory
            {
                UserId = userId,
                SongId = songId,
                Title = title,
                Artist = artist,
                Album = album,
                CoverArtUrl = coverArtUrl,
                PlayedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not record play history");
        }
    }

    /// <summary>Distinct, most-recent-first tracks the user has played.</summary>
    public async Task<List<MusicPlayHistory>> GetRecentlyPlayedAsync(int userId, int size = 30, CancellationToken ct = default)
    {
        if (userId <= 0) return [];
        try
        {
            await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
            List<MusicPlayHistory> recent = await db.Set<MusicPlayHistory>().AsNoTracking()
                .Where(h => h.UserId == userId)
                .OrderByDescending(h => h.PlayedAt)
                .Take(size * 4)
                .ToListAsync(ct);

            return recent
                .GroupBy(h => h.SongId)
                .Select(g => g.First())
                .Take(size)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read play history");
            return [];
        }
    }
}
