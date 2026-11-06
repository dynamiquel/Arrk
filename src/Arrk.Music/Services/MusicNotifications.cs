using Microsoft.Extensions.Logging;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Notifications;
using Arrk.Music.Models;

namespace Arrk.Music.Services;

/// <summary>
/// Projects music library activity (adds, grabs, imports) into the unified event log with a
/// user-facing notification attached, including best-effort album artwork. Producers write a
/// single <see cref="SystemEventRecord"/>; <see cref="NotifyingEventStore"/> fans the notification
/// out to the inbox and every configured channel, so this never talks to Discord itself.
/// </summary>
public sealed class MusicNotifications(
    IEventStore events,
    DeezerService? deezer = null,
    ILogger<MusicNotifications>? logger = null)
{
    public async Task ArtistAddedAsync(string artistName, CancellationToken ct = default)
    {
        string art = await ArtistArtAsync(artistName, ct);
        await RecordAsync(
            MusicNotificationTopics.ArtistAdded, "music.artist.added", "Artist added",
            $"{artistName} was added to the music library", artistName, art,
            EventSeverity.Info, fields: null, ct);
    }

    public async Task AlbumAddedAsync(Album album, CancellationToken ct = default)
    {
        string art = await AlbumArtAsync(album, ct);
        await RecordAsync(
            MusicNotificationTopics.AlbumAdded, "music.album.added", "Album added",
            $"{album.Title} by {album.ArtistName} was added to the music library",
            album.Title, art, EventSeverity.Info,
            fields: [new("Artist", album.ArtistName)], ct);
    }

    public async Task AlbumGrabbedAsync(Album? album, string releaseTitle, CancellationToken ct = default)
    {
        string art = album is null ? "" : await AlbumArtAsync(album, ct);
        await RecordAsync(
            MusicNotificationTopics.AlbumGrabbed, "music.album.grabbed", "Music grabbed",
            $"Grabbed {releaseTitle}", album?.Title ?? releaseTitle, art, EventSeverity.Info,
            fields: album is null ? null : [new("Album", album.Title), new("Artist", album.ArtistName)], ct);
    }

    /// <summary>A song was queued from a Soulseek peer (the track-level counterpart of a torrent grab).</summary>
    public async Task SoulseekGrabbedAsync(
        string artist, string title, string peer, string quality, CancellationToken ct = default)
    {
        await RecordAsync(
            DownloadNotificationTopics.SoulseekGrabbed, "downloads.soulseek.grabbed", "Music grabbed",
            $"Queued \"{title}\" by {artist} from Soulseek peer {peer} ({quality})",
            title, "", EventSeverity.Info,
            fields: [new("Artist", artist), new("Peer", peer), new("Quality", quality)], ct,
            category: EventCategory.Download);
    }

    /// <summary>
    /// An audit-only event (no inbox/channel notification) for a release search that grabbed nothing,
    /// so the Events page can explain why a song/album didn't download.
    /// </summary>
    public async Task SearchMissedAsync(string artist, string title, string reason, CancellationToken ct = default) =>
        await RecordAuditAsync(
            topic: null, "music.release.missed",
            $"Searched \"{title}\" by {artist}: {reason}", title, ct);

    // Lyrics
    // Only "lyrics found" is a notification; the rest are audit events, because a lyric lookup is a
    // frequent background chore and notifying every miss would be noise.

    /// <summary>Audit-only: a lyrics lookup started for a song.</summary>
    public async Task LyricsSearchStartedAsync(string artist, string title, CancellationToken ct = default) =>
        await RecordAuditAsync(
            MusicNotificationTopics.LyricsSearchStarted, "music.lyrics.search.started",
            $"Looking for lyrics for \"{title}\" by {artist}", title, ct);

    /// <summary>Lyrics were found and written beside the song.</summary>
    public async Task LyricsFoundAsync(string artist, string album, string title, LyricsTier tier, CancellationToken ct = default)
    {
        string art = await AlbumArtAsync(artist, album, ct);
        string sync = tier switch
        {
            LyricsTier.Word => "word-synced",
            LyricsTier.Line => "synced",
            _ => "plain"
        };

        List<NotificationField> fields = [new("Track", title)];
        if (!string.IsNullOrWhiteSpace(artist))
            fields.Add(new("Artist", artist));
        if (!string.IsNullOrWhiteSpace(album))
            fields.Add(new("Album", album));
        fields.Add(new("Sync", sync));

        await RecordAsync(
            MusicNotificationTopics.LyricsFound, "music.lyrics.found", "Lyrics found",
            $"Added {sync} lyrics for \"{title}\"" + (string.IsNullOrWhiteSpace(artist) ? "" : $" by {artist}"),
            title, art, EventSeverity.Success, fields, ct);
    }

    /// <summary>Audit-only: the provider had nothing for this song.</summary>
    public async Task LyricsNotFoundAsync(string artist, string title, CancellationToken ct = default) =>
        await RecordAuditAsync(
            MusicNotificationTopics.LyricsNotFound, "music.lyrics.not.found",
            $"No lyrics available for \"{title}\" by {artist}", title, ct);

    /// <summary>Audit-only: the song is instrumental, so there are no lyrics to find.</summary>
    public async Task LyricsInstrumentalAsync(string artist, string title, CancellationToken ct = default) =>
        await RecordAuditAsync(
            MusicNotificationTopics.LyricsInstrumental, "music.lyrics.instrumental",
            $"\"{title}\" by {artist} is instrumental", title, ct);

    /// <summary>Audit-only: the lyrics lookup itself failed (the provider was unreachable).</summary>
    public async Task LyricsSearchFailedAsync(string artist, string title, string reason, CancellationToken ct = default) =>
        await RecordAuditAsync(
            MusicNotificationTopics.LyricsSearchFailed, "music.lyrics.failed",
            $"Lyrics lookup for \"{title}\" by {artist} failed: {reason}", title, ct);

    /// <summary>
    /// Notification for a completed import. A single placed track is reported as a song (naming the
    /// track, its artist and album, with the album’s cover art); anything more is reported as an album.
    /// </summary>
    public async Task ImportedAsync(
        Album? album, string releaseName, IReadOnlyList<Track> placedTracks, CancellationToken ct = default)
    {
        if (album is not null && placedTracks.Count == 1)
            await SongImportedAsync(album, placedTracks[0], ct);
        else
            await AlbumImportedAsync(album, releaseName, placedTracks.Count, ct);
    }

    /// <summary>A single song was imported: name the track (with artist and album) and carry the art.</summary>
    public async Task SongImportedAsync(Album album, Track track, CancellationToken ct = default)
    {
        string art = await AlbumArtAsync(album, ct);
        string artist = string.IsNullOrWhiteSpace(album.ArtistName) ? "Unknown artist" : album.ArtistName;
        string albumTitle = string.IsNullOrWhiteSpace(album.Title) ? "Unknown album" : album.Title;
        await RecordAsync(
            MusicNotificationTopics.SongImported, "music.song.imported", "Song imported",
            $"\"{track.Title}\" by {artist} from \"{albumTitle}\" was added to the music library",
            track.Title, art, EventSeverity.Success,
            fields: [new("Track", track.Title), new("Artist", artist), new("Album", albumTitle)], ct);
    }

    public async Task AlbumImportedAsync(Album? album, string releaseName, int placed, CancellationToken ct = default)
    {
        string art = album is null ? "" : await AlbumArtAsync(album, ct);
        string label = string.IsNullOrWhiteSpace(album?.Title) ? releaseName : album!.Title;
        string artist = album?.ArtistName ?? "";
        string body = string.IsNullOrWhiteSpace(artist)
            ? $"{label} was added to the music library"
            : $"{label} by {artist} was added to the music library";

        List<NotificationField> fields = [];
        if (!string.IsNullOrWhiteSpace(artist))
            fields.Add(new("Artist", artist));
        if (placed > 0)
            fields.Add(new("Files", placed.ToString()));

        await RecordAsync(
            MusicNotificationTopics.AlbumImported, "music.album.imported", "Album imported",
            body, label, art, EventSeverity.Success,
            fields.Count > 0 ? fields : null, ct);
    }

    public async Task AlbumDownloadFailedAsync(Album? album, string releaseTitle, string reason, CancellationToken ct = default)
    {
        string art = album is null ? "" : await AlbumArtAsync(album, ct);
        List<NotificationField> fields = album is null
            ? [new("Reason", reason)]
            : [new("Album", album.Title), new("Artist", album.ArtistName), new("Reason", reason)];
        await RecordAsync(
            DownloadNotificationTopics.MusicAlbumDownloadFailed, "downloads.music.album.failed", "Music download failed",
            $"{album?.Title ?? releaseTitle} {reason}; blacklisted the source and searching for another",
            album?.Title ?? releaseTitle, art, EventSeverity.Warning, fields, ct,
            category: EventCategory.Download);
    }

    /// <summary>Best-effort absolute cover art: the album's own Deezer cover, then a Deezer lookup.</summary>
    async Task<string> AlbumArtAsync(Album album, CancellationToken ct)
    {
        if (IsAbsoluteHttpUrl(album.PosterUrl))
            return album.PosterUrl;

        return await AlbumArtAsync(album.ArtistName, album.Title, ct);
    }

    /// <summary>Best-effort album cover for a title/artist we hold no album row for (used by lyrics).</summary>
    async Task<string> AlbumArtAsync(string artist, string album, CancellationToken ct)
    {
        if (deezer is not null && !string.IsNullOrWhiteSpace(album))
        {
            try
            {
                string cover = await deezer.GetAlbumCoverAsync(artist, album, ct);
                if (IsAbsoluteHttpUrl(cover))
                    return cover;
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Deezer artwork lookup failed for {Title}", album);
            }
        }

        return "";
    }

    async Task<string> ArtistArtAsync(string artistName, CancellationToken ct)
    {
        if (deezer is null || string.IsNullOrWhiteSpace(artistName))
            return "";

        try
        {
            string image = await deezer.GetArtistImageAsync(artistName, ct);
            return IsAbsoluteHttpUrl(image) ? image : "";
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Deezer artwork lookup failed for {Artist}", artistName);
            return "";
        }
    }

    /// <summary>
    /// Records an event with no notification attached, so it appears on the Events page (and in the
    /// audit trail) without ever reaching the inbox or a delivery channel.
    /// </summary>
    async Task RecordAuditAsync(
        string? topic, string eventType, string body, string sourceName, CancellationToken ct)
    {
        try
        {
            await events.RecordAsync(new SystemEventRecord
            {
                Category = EventCategory.Media,
                EventType = eventType,
                Severity = EventSeverity.Info,
                Message = body,
                SourceName = sourceName,
                Topic = topic,
                Notification = null,
                Timestamp = DateTime.UtcNow
            }, ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to record music event {EventType}", eventType);
        }
    }

    async Task RecordAsync(
        string topic,
        string eventType,
        string title,
        string body,
        string sourceName,
        string art,
        EventSeverity severity,
        IReadOnlyList<NotificationField>? fields,
        CancellationToken ct,
        string category = EventCategory.Media)
    {
        DateTime now = DateTime.UtcNow;
        NotificationMessage notification = new()
        {
            Key = topic,
            Title = title,
            Body = body,
            Severity = severity,
            SourceName = sourceName,
            Timestamp = now,
            Fields = fields ?? [],
            Style = IsAbsoluteHttpUrl(art) ? new NotificationStyle { ImageUrl = art } : null
        };

        try
        {
            await events.RecordAsync(new SystemEventRecord
            {
                Category = category,
                EventType = eventType,
                Severity = severity,
                Message = body,
                SourceName = sourceName,
                Topic = topic,
                Notification = notification,
                Timestamp = now
            }, ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to record music notification {Topic}", topic);
        }
    }

    static bool IsAbsoluteHttpUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
