namespace Arrk.Navidrome.Models;

/// <summary>
/// Ownership of a Navidrome playlist. Navidrome playlists are created through one shared API
/// account, so a row records which Arrk account created a playlist. Playlists with no row are
/// shared and shown only to admins.
/// </summary>
public class MusicPlaylistOwner
{
    /// <summary>The Navidrome/Subsonic playlist id (the primary key, one owner per playlist).</summary>
    public string PlaylistId { get; set; } = "";

    public int OwnerUserId { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A per-user play history entry, recorded when the in-site player starts a track. Navidrome's own
/// "recently played" is tied to the shared API account, so Arrk keeps its own per-user history.
/// </summary>
public class MusicPlayHistory
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public string SongId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string CoverArtUrl { get; set; } = "";

    public DateTime PlayedAt { get; set; }
}

/// <summary>
/// A user's subscription to an external (Deezer) playlist, Apple-Music style: it is projected into the
/// user's Navidrome playlists and, when <see cref="AutoDownload"/> is on, songs that aren't in the
/// library yet are pulled in via Lidarr on each sync.
/// </summary>
public class MusicSubscription
{
    public int Id { get; set; }

    public int OwnerUserId { get; set; }

    /// <summary>Provider key (currently only "deezer").</summary>
    public string Source { get; set; } = "deezer";

    /// <summary>The provider's playlist id.</summary>
    public long SourcePlaylistId { get; set; }

    public string Name { get; set; } = "";
    public string ArtUrl { get; set; } = "";

    /// <summary>Download missing songs' albums automatically on each sync.</summary>
    public bool AutoDownload { get; set; } = true;

    /// <summary>The Navidrome playlist this subscription is projected into (owned by the user).</summary>
    public string NavidromePlaylistId { get; set; } = "";

    public DateTime CreatedAt { get; set; }
    public DateTime LastSyncedAt { get; set; }
    public int TrackCount { get; set; }
    public int MatchCount { get; set; }
    public string LastError { get; set; } = "";

    /// <summary>JSON of the source playlist's tracks (title/artist/album/art + matched Navidrome song id), so
    /// the library can show every song the subscription expects, greyed out when not yet in the library.</summary>
    public string TracksJson { get; set; } = "";
}
