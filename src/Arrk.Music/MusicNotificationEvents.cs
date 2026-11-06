using Arrk.Core.Notifications;

namespace Arrk.Music;

/// <summary>Hierarchical notification topics for music (owned by the music module).</summary>
public static class MusicNotificationTopics
{
    public const string ArtistAdded = "media.music.artist.added";
    public const string AlbumAdded = "media.music.album.added";
    public const string AlbumGrabbed = "media.music.album.grabbed";
    public const string AlbumImported = "media.music.album.imported";
    public const string SongImported = "media.music.song.imported";

    /// <summary>A lyrics lookup ran for a song (audit only, no inbox/channel notification).</summary>
    public const string LyricsSearchStarted = "media.music.lyrics.search.started";

    /// <summary>Lyrics were found and written beside a song (notified).</summary>
    public const string LyricsFound = "media.music.lyrics.found";

    /// <summary>A lyrics lookup found nothing available (audit only).</summary>
    public const string LyricsNotFound = "media.music.lyrics.not.found";

    /// <summary>A song is instrumental, so there are no lyrics to find (audit only).</summary>
    public const string LyricsInstrumental = "media.music.lyrics.instrumental";

    /// <summary>A lyrics lookup failed against the provider (audit only).</summary>
    public const string LyricsSearchFailed = "media.music.lyrics.search.failed";
}

/// <summary>
/// Contributes music/library notification events. Grabs live in the module's native acquisition
/// service, and the topic catalogue belongs here with the rest of the music domain.
/// </summary>
public sealed class MusicNotificationEvents : INotificationEventProvider
{
    public IReadOnlyList<NotificationEventDefinition> Events { get; } =
    [
        new(MusicNotificationTopics.ArtistAdded, true, "An artist was added to the music library", "🎤", 0x1DB954),
        new(MusicNotificationTopics.AlbumAdded, true, "An album was added to the music library", "💿", 0x1DB954),
        new(MusicNotificationTopics.AlbumGrabbed, true, "A music release was grabbed for download", "🎵", 0x9B59B6),
        new(MusicNotificationTopics.AlbumImported, true, "A grabbed album was imported into the library", "🎉", 0x2ECC71),
        new(MusicNotificationTopics.SongImported, true, "A grabbed song was imported into the library", "🎵", 0x2ECC71),
        new(MusicNotificationTopics.LyricsFound, true, "Lyrics were found for a song", "🎼", 0x3498DB),
    ];

    public IReadOnlyList<NotificationGroupDefinition> Groups { get; } =
    [
        new("media.music", "Music", "Music library activity"),
        new("media.music.album", "Albums", "Album adds, grabs and imports"),
        new("media.music.song", "Songs", "Song imports"),
        new("media.music.artist", "Artists", "Artist adds"),
        new("media.music.lyrics", "Lyrics", "Lyrics lookups"),
    ];
}
