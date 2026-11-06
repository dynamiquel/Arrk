using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Arrk.Ai;
using Arrk.Core.Enums;
using Arrk.Core.Lifecycle;
using Arrk.Core.Models;
using Arrk.Core.Playback;
using Arrk.Core.Requests;
using Arrk.Music.Components.Shared;
using Arrk.Music.Models;
using Arrk.Navidrome;
using Arrk.Requests;

namespace Arrk.Music.Services;

/// <summary>
/// The music action surface for the assistant: request an album/artist, manage watchlists, delete a
/// downloaded album and start the in-site player. It uses the same catalog, request handler and
/// lifecycle provider as the Music UI.
/// </summary>
public sealed class MusicAssistantActionSource(
    MusicCatalogService catalog,
    NavidromeService navidrome,
    WatchlistService watchlists,
    IEnumerable<IManagedMediaProvider> managedProviders,
    ILogger<MusicAssistantActionSource> logger) : IAssistantMediaActionSource
{
    public string Key => MusicAssistantMediaSource.SourceKey;

    public bool IsAvailable => true;

    public IReadOnlyList<AssistantActionKind> SupportedActions =>
    [
        AssistantActionKind.Request,
        AssistantActionKind.AddToWatchlist,
        AssistantActionKind.RemoveFromWatchlist,
        AssistantActionKind.Delete,
        AssistantActionKind.Play
    ];

    public async Task<AssistantActionOutcome> ExecuteAsync(
        AssistantActionKind action,
        string id,
        string? option,
        bool confirmed,
        AssistantToolContext context,
        CancellationToken ct)
    {
        if (Parse(id) is not ParsedMusicId parsed)
            return AssistantActionOutcome.Fail("I could not read that music id.");

        return action switch
        {
            AssistantActionKind.Request => await RequestAsync(parsed, option, context, ct),
            AssistantActionKind.AddToWatchlist => await WatchlistAsync(parsed, option, add: true, context, ct),
            AssistantActionKind.RemoveFromWatchlist => await WatchlistAsync(parsed, option, add: false, context, ct),
            AssistantActionKind.Delete => await DeleteAsync(parsed, confirmed, context, ct),
            AssistantActionKind.Play => await PlayAsync(parsed, ct),
            _ => AssistantActionOutcome.Fail($"Music cannot {action.ToString().ToLowerInvariant()}.")
        };
    }

    //  Request

    async Task<AssistantActionOutcome> RequestAsync(
        ParsedMusicId parsed, string? quality, AssistantToolContext context, CancellationToken ct)
    {
        RequestService? requests = context.Services.GetService(typeof(RequestService)) as RequestService;
        if (requests is null || !requests.Enabled)
            return AssistantActionOutcome.Fail("Requests are turned off for this Arrk instance.");

        MusicItem? item = await ResolveAsync(parsed, ct);
        if (item is null)
            return AssistantActionOutcome.Fail("I could not load that album or artist from Deezer.");

        string trackTitle = FindTrack(item, parsed)?.Title ?? item.Title;
        RequestTarget target = parsed.Kind switch
        {
            AssistantMediaKind.Artist => MusicRequestTargets.ForArtist(item),
            AssistantMediaKind.Track => new RequestTarget(
                MusicMediaDomains.Music,
                RequestKind.Track,
                ProviderIdMap.Build(
                    (MusicProviderIds.DeezerArtist, item.ArtistDeezerId > 0
                        ? item.ArtistDeezerId.ToString(CultureInfo.InvariantCulture) : null),
                    (MusicProviderIds.Deezer, item.AlbumDeezerId > 0
                        ? item.AlbumDeezerId.ToString(CultureInfo.InvariantCulture) : null)),
                trackTitle,
                item.Year > 0 ? item.Year : null,
                PosterUrl: item.ArtUrl),
            _ => MusicRequestTargets.ForAlbum(item)
        };

        RequestOperationResult result = await requests.CreateAsync(
            context.UserId, context.Username, target,
            string.IsNullOrWhiteSpace(quality) ? null : quality.Trim(), ct);

        if (!result.Success)
            return AssistantActionOutcome.Fail(result.Message ?? "The request could not be created.");

        string status = result.Request?.Status.ToString() ?? "Pending";
        string message = status switch
        {
            "Approved" => $"Requested {target.Title} and it was approved automatically.",
            "Failed" => $"Requested {target.Title}, but fulfilment failed: {result.Request?.DecisionReason ?? "check Requests"}.",
            _ => $"Requested {target.Title}. It is waiting for approval."
        };
        return AssistantActionOutcome.Ok(message);
    }

    //  Watchlist

    async Task<AssistantActionOutcome> WatchlistAsync(
        ParsedMusicId parsed, string? listName, bool add, AssistantToolContext context, CancellationToken ct)
    {
        MusicItem? item = await ResolveAsync(parsed, ct);
        if (item is null)
            return AssistantActionOutcome.Fail("I could not load that album or artist.");

        string title = parsed.Kind == AssistantMediaKind.Track
            ? FindTrack(item, parsed)?.Title ?? item.Title
            : item.Title;
        string itemKey = parsed.Kind switch
        {
            AssistantMediaKind.Artist => $"music:artist:{Normalize(item.Title)}",
            AssistantMediaKind.Track => MediaItemKeys.MusicAlbum(item.ArtistName, item.AlbumName),
            _ => MediaItemKeys.MusicAlbum(item.ArtistName, item.Title)
        };
        if (string.IsNullOrWhiteSpace(itemKey))
            itemKey = $"music:album:{Normalize(item.Title)}";

        if (!add)
        {
            IReadOnlyList<int> containing = await watchlists.GetContainingAsync(
                context.UserId, WatchActivityServices.Music, itemKey, ct);
            if (containing.Count == 0)
                return AssistantActionOutcome.Fail($"{title} is not on a watchlist.");

            int removed = 0;
            foreach (int listId in containing)
            {
                if (await watchlists.RemoveItemAsync(context.UserId, listId, itemKey, ct))
                    removed++;
            }

            return removed > 0
                ? AssistantActionOutcome.Ok($"Removed {title} from the watchlist.")
                : AssistantActionOutcome.Fail($"I could not remove {title} from the watchlist.");
        }

        int targetList = await ResolveListAsync(context.UserId, listName, ct);
        if (targetList == 0)
            return AssistantActionOutcome.Fail("I could not create a watchlist.");

        WatchlistItemRef reference = new(
            WatchActivityServices.Music,
            MusicMediaDomains.Music,
            itemKey,
            title,
            parsed.Kind == AssistantMediaKind.Artist ? "Artist" : item.ArtistName,
            item.ArtUrl,
            item.Year > 0 ? item.Year : null);

        bool ok = await watchlists.AddItemAsync(context.UserId, targetList, reference, ct);
        return ok
            ? AssistantActionOutcome.Ok($"Added {title} to your watchlist.")
            : AssistantActionOutcome.Fail($"I could not add {title} to the watchlist.");
    }

    async Task<int> ResolveListAsync(int userId, string? name, CancellationToken ct)
    {
        IReadOnlyList<WatchlistSummary> lists = await watchlists.GetForUserAsync(userId, ct);
        if (!string.IsNullOrWhiteSpace(name))
        {
            WatchlistSummary? named = lists.FirstOrDefault(l =>
                string.Equals(l.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (named is not null)
                return named.Id;
        }

        if (lists.Count > 0)
            return lists[0].Id;

        string listName = string.IsNullOrWhiteSpace(name) ? "Watchlist" : name.Trim();
        WatchlistDetail? created = await watchlists.CreateAsync(userId, listName, null, ct);
        return created?.Id ?? 0;
    }

    //  Delete

    async Task<AssistantActionOutcome> DeleteAsync(
        ParsedMusicId parsed, bool confirmed, AssistantToolContext context, CancellationToken ct)
    {
        if (!context.IsAdmin)
            return AssistantActionOutcome.Fail("Only an administrator can delete music.");

        MusicItem? item = await ResolveAsync(parsed, ct);
        if (item is null)
            return AssistantActionOutcome.Fail("I could not load that album or artist.");
        if (item.LibraryId <= 0 && parsed.Kind != AssistantMediaKind.Artist)
            return AssistantActionOutcome.Fail($"{item.Title} is not in the library.");

        string title = parsed.Kind == AssistantMediaKind.Track
            ? FindTrack(item, parsed)?.Title ?? item.Title
            : item.Title;

        if (!confirmed)
            return AssistantActionOutcome.Confirm(
                $"Delete {title} and its files from the library? Ask the user to confirm.",
                $"Delete {title}");

        IManagedMediaProvider? provider = managedProviders
            .FirstOrDefault(p => p.Key == WatchActivityServices.Music);
        if (provider is null)
            return AssistantActionOutcome.Fail("The music library cannot be deleted from here.");

        if (parsed.Kind == AssistantMediaKind.Track)
        {
            MusicTrackItem? track = FindTrack(item, parsed);
            int trackId = parsed.LibraryScope ? parsed.TrackId ?? 0 : track?.TrackId ?? 0;
            if (trackId <= 0)
                return AssistantActionOutcome.Fail($"I could not find the library file for {title}.");

            bool deleted = await catalog.DeleteTrackFileAsync(item.LibraryId, trackId, ct);
            catalog.InvalidateLibraryCache();
            return deleted
                ? AssistantActionOutcome.Ok($"Deleted the file for {title}.")
                : AssistantActionOutcome.Fail($"I could not delete the file for {title}.");
        }

        if (parsed.Kind == AssistantMediaKind.Artist)
        {
            List<MusicItem> albums = await catalog.GetLibraryArtistAlbumsAsync(item.LibraryId, ct);
            int deleted = 0;
            foreach (MusicItem album in albums.Where(a => a.LibraryId > 0))
            {
                if (await DeleteAlbumViaProviderAsync(provider, album, ct))
                    deleted++;
            }

            await catalog.RemoveArtistAsync(item, deleteFiles: true, ct);
            catalog.InvalidateLibraryCache();
            return AssistantActionOutcome.Ok(
                deleted == 0
                    ? $"Removed {item.Title} from the library."
                    : $"Deleted {item.Title} and its {deleted} albums.");
        }

        bool ok = await DeleteAlbumViaProviderAsync(provider, item, ct);
        catalog.InvalidateLibraryCache();
        return ok
            ? AssistantActionOutcome.Ok($"Deleted {item.Title} and its files.")
            : AssistantActionOutcome.Fail($"I could not delete {item.Title}.");
    }

    async Task<bool> DeleteAlbumViaProviderAsync(
        IManagedMediaProvider provider, MusicItem album, CancellationToken ct)
    {
        ManagedMediaItem managed = new(
            provider.Key,
            MediaItemKeys.MusicAlbum(album.ArtistName, album.Title),
            MusicMediaDomains.Music,
            "album",
            album.Title,
            album.ArtistName,
            album.ArtUrl,
            album.SizeOnDisk,
            DateTime.UtcNow,
            IsBookmarked: false,
            BookmarkLabel: null,
            LibraryId: album.LibraryId.ToString(CultureInfo.InvariantCulture));

        ManagedMediaDeleteResult result = await provider.DeleteAsync(managed, ct);
        if (!result.Success)
            logger.LogDebug("Assistant music delete failed for {Title}: {Message}", album.Title, result.Message);
        return result.Success;
    }

    //  Play

    async Task<AssistantActionOutcome> PlayAsync(ParsedMusicId parsed, CancellationToken ct)
    {
        if (!navidrome.IsConfigured)
            return AssistantActionOutcome.Fail("Navidrome is not configured, so there is no music player to start.");

        MusicItem? item = await ResolveAsync(parsed, ct);
        if (item is null)
            return AssistantActionOutcome.Fail("I could not load that music from the library.");

        List<Dictionary<string, object?>> queue = [];

        if (parsed.Kind == AssistantMediaKind.Track)
        {
            string trackTitle = FindTrack(item, parsed)?.Title ?? item.Title;
            string? songId = await navidrome.FindSongIdAsync(trackTitle, item.ArtistName, ct);
            if (songId is null)
                return AssistantActionOutcome.Fail($"I could not find {trackTitle} in Navidrome.");

            queue.Add(QueueItem(songId, trackTitle, item.ArtistName, item.Title, item.ArtUrl));
        }
        else if (parsed.Kind == AssistantMediaKind.Artist)
        {
            List<SubsonicSong> songs = await navidrome.SearchSongsAsync(item.Title, 25, ct);
            foreach (SubsonicSong song in songs)
                queue.Add(QueueItem(song.Id, song.Title, song.Artist, song.Album, NavidromeService.CoverArtUrl(song.CoverArt)));
        }
        else
        {
            string? albumId = await navidrome.FindAlbumIdAsync(item.Title, item.ArtistName, ct);
            if (albumId is null)
                return AssistantActionOutcome.Fail($"I could not find {item.Title} in Navidrome.");

            SubsonicAlbumWithSongs? album = await navidrome.GetAlbumAsync(albumId, ct);
            if (album is null || album.Song.Count == 0)
                return AssistantActionOutcome.Fail($"{item.Title} has no playable songs in Navidrome.");

            foreach (SubsonicSong song in album.Song)
            {
                queue.Add(QueueItem(song.Id, song.Title, song.Artist, song.Album,
                    string.IsNullOrWhiteSpace(song.CoverArt)
                        ? NavidromeService.CoverArtUrl(album.CoverArt)
                        : NavidromeService.CoverArtUrl(song.CoverArt)));
            }
        }

        if (queue.Count == 0)
            return AssistantActionOutcome.Fail("There is nothing playable in Navidrome for that item.");

        string label = parsed.Kind == AssistantMediaKind.Track
            ? FindTrack(item, parsed)?.Title ?? item.Title
            : item.Title;

        return AssistantActionOutcome.Ok(
            $"Ready to play {label}.",
            new AssistantActionLink(
                AssistantActionLinkKind.MusicPlay,
                $"Play {label}",
                Payload: JsonSerializer.Serialize(queue)));
    }

    static Dictionary<string, object?> QueueItem(
        string songId, string title, string artist, string album, string? cover) => new()
    {
        ["id"] = songId,
        ["title"] = title,
        ["artist"] = artist,
        ["album"] = album,
        ["cover"] = cover ?? "",
        ["stream"] = NavidromeService.PlayerStreamUrl(songId)
    };

    //  Helpers

    async Task<MusicItem?> ResolveAsync(ParsedMusicId parsed, CancellationToken ct)
    {
        if (parsed.Kind == AssistantMediaKind.Artist)
        {
            MusicItem? artist;
            if (parsed.LibraryScope)
            {
                (List<Artist> artists, _) = await catalog.GetLibraryAsync(ct);
                Artist? row = artists.FirstOrDefault(a => a.Id == parsed.Id);
                artist = row is null ? null : MusicItem.FromArtist(row);
            }
            else
            {
                artist = await catalog.GetArtistAsync(parsed.Id, ct);
            }

            // A Deezer-scope artist may still be in the library; prefer the library row so delete
            // and watchlist use the real library id.
            if (artist is { IsInLibrary: true, LibraryId: 0 })
            {
                MusicItem? libraryArtist = (await catalog.GetLibraryViewAsync(ct)).Artists
                    .FirstOrDefault(a => a.DeezerId == artist.DeezerId);
                if (libraryArtist is not null)
                    return libraryArtist;
            }

            return artist;
        }

        MusicItem? album = parsed.LibraryScope
            ? await catalog.GetLibraryAlbumAsync(parsed.Id, ct)
            : await catalog.GetAlbumAsync(parsed.Id, ct);

        if (album is not null && album.LibraryId == 0 && album.IsInLibrary)
            album = await catalog.FindLibraryAlbumByNameAsync(album.ArtistName, album.Title, ct);

        return album;
    }

    static ParsedMusicId? Parse(string id)
    {
        string[] parts = id.Split(':');
        if (parts.Length < 4
            || !string.Equals(parts[0], MusicAssistantMediaSource.SourceKey, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string kind = parts[1].ToLowerInvariant();
        bool library = string.Equals(parts[2], "lib", StringComparison.OrdinalIgnoreCase);
        if (!int.TryParse(parts[3], out int entityId) || entityId <= 0)
            return null;

        int? trackId = parts.Length >= 5 && int.TryParse(parts[4], out int track) ? track : null;

        return kind switch
        {
            "album" => new ParsedMusicId(AssistantMediaKind.Album, library, entityId, null, null),
            "artist" => new ParsedMusicId(AssistantMediaKind.Artist, library, entityId, null, null),
            "track" => new ParsedMusicId(AssistantMediaKind.Track, library, entityId, trackId, null),
            _ => null
        };
    }

    static MusicTrackItem? FindTrack(MusicItem album, ParsedMusicId parsed)
    {
        if (parsed.Kind != AssistantMediaKind.Track || parsed.TrackId is null)
            return null;

        return parsed.LibraryScope
            ? album.Tracks.FirstOrDefault(t => t.TrackId == parsed.TrackId)
            : album.Tracks.FirstOrDefault(t => t.Number == parsed.TrackId);
    }

    static string Normalize(string? value) =>
        new((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    readonly record struct ParsedMusicId(
        AssistantMediaKind Kind,
        bool LibraryScope,
        int Id,
        int? TrackId,
        string? TrackTitle);
}
