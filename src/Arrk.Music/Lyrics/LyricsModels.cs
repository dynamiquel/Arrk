using System.Text.Json.Serialization;
using Arrk.Core.Models;

namespace Arrk.Music.Lyrics;

/// <summary>
/// One lyrics record as a provider (LRCLIB) returns it. Kept as the provider's own shape so the
/// matcher can score candidates before any of Arrk's policy (tier, sidecar format) is applied.
/// </summary>
public sealed record LyricsCandidate
{
    [JsonPropertyName("trackName")] public string TrackName { get; init; } = "";
    [JsonPropertyName("artistName")] public string ArtistName { get; init; } = "";
    [JsonPropertyName("albumName")] public string AlbumName { get; init; } = "";
    [JsonPropertyName("duration")] public double DurationSeconds { get; init; }
    [JsonPropertyName("instrumental")] public bool Instrumental { get; init; }
    [JsonPropertyName("plainLyrics")] public string PlainLyrics { get; init; } = "";
    [JsonPropertyName("syncedLyrics")] public string SyncedLyrics { get; init; } = "";

    /// <summary>True when the record carries word/syllable-level timing.</summary>
    [JsonPropertyName("hasWordSync")] public bool HasWordSync { get; init; }

    /// <summary>The provider's richer lyrics file (AML/YAML with word timing) when it has one.</summary>
    [JsonPropertyName("lyricsfile")] public string LyricsFile { get; init; } = "";

    /// <summary>True when the record carries anything at all worth writing.</summary>
    [JsonIgnore]
    public bool HasLyrics => Instrumental || !string.IsNullOrWhiteSpace(SyncedLyrics) || !string.IsNullOrWhiteSpace(PlainLyrics);
}

/// <summary>
/// The lyrics chosen for one track, ready to be written beside its audio file. <see cref="Extension"/>
/// is what Navidrome keys its loader off (<c>.lrc</c> for timed lyrics, <c>.txt</c> for plain);
/// <see cref="Tier"/> records how well synced they are so the UI can choose a renderer.
/// </summary>
public sealed record LyricsContent(string Text, string Extension, LyricsTier Tier)
{
    public const string LrcExtension = ".lrc";
    public const string TextExtension = ".txt";
}

/// <summary>
/// Thrown when a lyrics provider could not answer at all (network error, HTTP 429/5xx). Distinct from
/// "no lyrics exist" so the caller can mark the track <see cref="LyricsState.Failed"/>, a transient
/// state that is retried, instead of <see cref="LyricsState.NotFound"/>, which is not.
/// </summary>
public sealed class LyricsProviderUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
