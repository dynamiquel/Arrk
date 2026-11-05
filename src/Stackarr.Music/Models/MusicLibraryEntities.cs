using Stackarr.Core.Models;

namespace Stackarr.Music.Models;

/// <summary>What a <see cref="LibraryIntent"/> refers to.</summary>
public enum LibraryIntentKind
{
    Artist,
    Album,
    Track
}

/// <summary>Lifecycle of a library intent.</summary>
public enum LibraryIntentState
{
    Wanted,
    Acquired,
    Ignored
}

/// <summary>
/// A music artist owned by Stackarr. <see cref="DeezerId"/> is the canonical identifier (matching
/// the Deezer artist id a request carries); 0 when the artist was only ever seen from a download.
/// </summary>
public class MusicArtist
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string SortName { get; set; } = "";
    public long DeezerId { get; set; }
    public string Overview { get; set; } = "";
    public string GenresJson { get; set; } = "";
    public double? Rating { get; set; }
    public bool Monitored { get; set; } = true;
    public DateTime Added { get; set; }
    public string PosterUrl { get; set; } = "";
    public string FanartUrl { get; set; } = "";

    public List<MusicAlbum> Albums { get; set; } = [];
}

/// <summary>A music album (a release/album as the discovery layer sees it).</summary>
public class MusicAlbum
{
    public int Id { get; set; }
    public int ArtistId { get; set; }
    public MusicArtist? Artist { get; set; }
    public string Title { get; set; } = "";
    public string AlbumType { get; set; } = "";
    public DateTime? ReleaseDate { get; set; }
    public long DeezerId { get; set; }
    public string Overview { get; set; } = "";
    public string PosterUrl { get; set; } = "";
    public bool Monitored { get; set; } = true;
    public int DurationSeconds { get; set; }
    public int TotalTrackCount { get; set; }

    /// <summary>When Stackarr first added/holds this album (used for "Recently Added").</summary>
    public DateTime Added { get; set; }

    /// <summary>When the rescue last tried to identify this album (so it isn't retried every run).</summary>
    public DateTime? IdentificationAttemptedAt { get; set; }

    /// <summary>When the re-tag pass last processed this album (so it isn't retried every run).</summary>
    public DateTime? RetagAttemptedAt { get; set; }

    public List<MusicTrack> Tracks { get; set; } = [];
}

/// <summary>A single track appearance on an album.</summary>
public class MusicTrack
{
    public int Id { get; set; }
    public int AlbumId { get; set; }
    public int ArtistId { get; set; }
    public string Title { get; set; } = "";
    public int DiscNumber { get; set; } = 1;
    public int TrackNumber { get; set; }
    public int AbsoluteTrackNumber { get; set; }
    public int DurationSeconds { get; set; }
    public long DeezerId { get; set; }
    public bool Monitored { get; set; } = true;

    /// <summary>True once Stackarr has placed this track's file in the library.</summary>
    public bool HasFile { get; set; }

    /// <summary>Path of the placed file (relative to the library root) once imported; used for
    /// per-track media info. Empty when the track has no file.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>Where this track's lyrics lookup stands (see <see cref="LyricsState"/>).</summary>
    public LyricsState LyricsState { get; set; } = LyricsState.None;

    /// <summary>The best sync level obtained for this track's lyrics; <see cref="LyricsTier.None"/>
    /// until lyrics are found.</summary>
    public LyricsTier LyricsTier { get; set; } = LyricsTier.None;

    /// <summary>When the lyrics job last looked at this track (null = never).</summary>
    public DateTime? LyricsCheckedAt { get; set; }
}

/// <summary>
/// What the user asked Stackarr to hold. This is the monitored/wanted state for the native library.
/// </summary>
public class LibraryIntent
{
    public int Id { get; set; }
    public LibraryIntentKind Kind { get; set; }
    public int TargetId { get; set; }
    public LibraryIntentState State { get; set; } = LibraryIntentState.Wanted;
    public int QualityProfileId { get; set; }
    public string Source { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public DateTime RequestedAt { get; set; }
}

/// <summary>An acquisition attempt for a track/album, from a torrent or Soulseek source.</summary>
public class GrabRecord
{
    public int Id { get; set; }
    public int AlbumId { get; set; }
    public string Source { get; set; } = "torrent";
    public string Identifier { get; set; } = "";
    public string ReleaseTitle { get; set; } = "";
    public string State { get; set; } = "Grabbed";
    public long TorrentId { get; set; }
    public double Progress { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>The Deezer track id this grab was requested for (0 when unknown). Lets the importer
    /// bind the downloaded file to the exact song, title, performer, album, instead of guessing
    /// from the peer's folder, and lets Discovery match the item to an active download.</summary>
    public long DeezerTrackId { get; set; }

    /// <summary>The Deezer album id this grab was requested for (0 when unknown). Playlist/album
    /// requests know the release up front, so the importer can bind to it exactly.</summary>
    public long DeezerAlbumId { get; set; }
}

/// <summary>
/// One row in Stackarr's music acquisition queue: a song or album we want but don't hold yet. A
/// recurring job drains a batch each run, least-failed/oldest first; a failed attempt bumps
/// <see cref="Attempts"/> so the request drops behind healthier ones next time. Once a request has
/// been tried <see cref="MaxAttempts"/> times it is parked: the drain job leaves it alone until the
/// user explicitly searches for it again (which resets the counter).
/// </summary>
public class MusicAcquireQueueItem
{
    /// <summary>How many automatic attempts a request gets before it is parked.</summary>
    public const int MaxAttempts = 50;

    /// <summary>An in-flight row older than this is considered abandoned (the grab/import never
    /// came back) and is eligible for the drain job again, so a lost import cannot stall it forever.</summary>
    public const int StaleInFlightHours = 6;

    public int Id { get; set; }

    /// <summary>Stable identity of the request, so re-requesting never duplicates a row.</summary>
    public string Key { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public int AlbumId { get; set; }
    public long DeezerTrackId { get; set; }
    public bool RequireArtist { get; set; } = true;

    /// <summary>How many times this request has failed. Higher = further back in the queue.</summary>
    public int Attempts { get; set; }

    /// <summary>True while a grab is in flight, so the drain job doesn't re-grab it every minute.</summary>
    public bool InFlight { get; set; }

    public string LastError { get; set; } = "";
    public DateTime RequestedAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
}

/// <summary>Well-known <see cref="GrabRecord.State"/> values shared by the importer and failure handling.</summary>
public static class GrabState
{
    public const string Grabbed = "Grabbed";
    public const string Imported = "Imported";
    public const string Failed = "Failed";
}

/// <summary>The source a <see cref="GrabRecord"/> came from.</summary>
public static class GrabSource
{
    public const string Torrent = "torrent";
    public const string Soulseek = "soulseek";
}
