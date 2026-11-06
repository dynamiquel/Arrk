using System.ComponentModel;

namespace Arrk.Music;

/// <summary>
/// Arrk-owned music settings. There is no quality profile sync; the acquisition scorer applies
/// the codec preference directly.
/// </summary>
public class MusicConfig
{
    [Description("Enable the music module (search, acquisition, library)")]
    public bool Enabled { get; set; } = true;

    [Description("Root folder for the music library (default /music)")]
    public string RootFolder { get; set; } = "/music";

    [Description("Preferred source for album requests: torrent | soulseek")]
    public string AlbumSource { get; set; } = "torrent";

    [Description("Preferred source for single-track requests: soulseek | torrent")]
    public string TrackSource { get; set; } = "soulseek";

    [Description("Use torrent indexers (Prowlarr + Transmission) for music. Turn off to acquire from Soulseek only.")]
    public bool UseTorrents { get; set; } = true;

    [Description("Use Soulseek (slskd) for music. Turn off to acquire from torrent indexers only.")]
    public bool UseSoulseek { get; set; } = true;

    [Description("Minimum acceptable Soulseek bitrate in kbps (lower quality is only used as a last resort)")]
    public int SoulseekMinBitrate { get; set; } = 192;

    [Description("Re-identify albums that couldn't be matched to Deezer (bad folder/title names) and rebind their tracks to the right release")]
    public bool RescueUnidentified { get; set; } = true;

    [Description("When a track still can't be identified, delete the untrusted file and re-acquire a fresh copy")]
    public bool DeleteUntrustedTracks { get; set; } = true;

    [Description("Quality profiles offered when adding an album (mirrors the movie/TV profile picker)")]
    public List<MusicQualityProfile> Profiles { get; set; } = DefaultProfiles();

    [Description("Synced lyrics: fetched in the background and written beside each audio file")]
    public LyricsConfig Lyrics { get; set; } = new();

    /// <summary>Best Available / Lossless / Lossy, the audio equivalent of the movie/TV defaults.</summary>
    public static List<MusicQualityProfile> DefaultProfiles() =>
    [
        new() { Name = "Best Available", PreferLossless = true, LosslessOnly = false },
        new() { Name = "Lossless", PreferLossless = true, LosslessOnly = true },
        new() { Name = "Lossy", PreferLossless = false, LosslessOnly = false }
    ];
}

/// <summary>One music quality profile (codec preference for acquisition).</summary>
public class MusicQualityProfile
{
    [Description("Profile name shown in the picker")]
    public string Name { get; set; } = "";

    [Description("Prefer lossless (FLAC/ALAC) releases when available")]
    public bool PreferLossless { get; set; } = true;

    [Description("Only accept lossless releases")]
    public bool LosslessOnly { get; set; }
}

/// <summary>
/// Lyrics settings. Lyrics are fetched by a background sweep (never on the import path) from LRCLIB
/// and written beside each audio file, so Navidrome and every Subsonic client can serve them. See
/// <c>Arrk.Music.Lyrics</c>.
/// </summary>
public class LyricsConfig
{
    [Description("Find and download lyrics for library songs in the background")]
    public bool Enabled { get; set; } = true;

    [Description("Songs looked up per run. Keep this modest, LRCLIB is a free, rate-limited service")]
    public int BatchSize { get; set; } = 40;

    [Description("Delay between lyric requests in milliseconds, to avoid overloading the provider")]
    public int RequestDelayMs { get; set; } = 400;

    [Description("Ask Navidrome to rescan after new lyrics are written so it indexes them immediately")]
    public bool RescanNavidrome { get; set; } = true;
}
