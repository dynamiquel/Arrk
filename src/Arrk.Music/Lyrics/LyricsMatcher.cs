using Arrk.Core.Models;

namespace Arrk.Music.Lyrics;

/// <summary>One provider query derived from Arrk's metadata.</summary>
public sealed record LyricsQuery(string? TrackName, string? ArtistName, string? Query)
{
    public bool IsStructured => !string.IsNullOrWhiteSpace(TrackName) && !string.IsNullOrWhiteSpace(ArtistName);

    /// <summary>A title-only structured query (no artist filter).</summary>
    public bool IsTitleOnly => !string.IsNullOrWhiteSpace(TrackName) && string.IsNullOrWhiteSpace(ArtistName);
}

/// <summary>
/// Turns Arrk's Deezer metadata into provider queries and verifies what comes back.
///
/// There is no identifier to join on, LRCLIB records carry no ISRC, MBID or Deezer id, so lyrics
/// resolution is fuzzy by design. Deezer's metadata does not make that easy: titles carry
/// disambiguation tails, album names drift between releases, and a soundtrack credits the composer or
/// a "Various Artists" placeholder where the provider credits the performer. The strategy is
/// therefore: query from narrow to wide, prefer a candidate whose artist *and* title agree, and fall
/// back to an exact title with a closely matching duration when no artist-verified record exists.
/// Binding a same-named different song's lyrics is worse than showing none.
/// </summary>
internal static class LyricsMatcher
{
    /// <summary>Durations this close are treated as the same recording.</summary>
    const int DurationToleranceSeconds = 5;

    /// <summary>An exact title+artist match tolerates a wider gap (different masters of one song).</summary>
    const int ExactDurationToleranceSeconds = 30;

    /// <summary>
    /// With no matching artist there is nothing left to confirm the song with, so the duration has to
    /// carry the whole decision and the gate is tight.
    /// </summary>
    const int TitleOnlyDurationToleranceSeconds = 3;

    /// <summary>
    /// Credits that carry no information. No provider record is credited to these, so verifying
    /// against them can only fail, and asking LRCLIB to filter its search by such a broad artist
    /// reliably earns a <c>503</c>.
    /// </summary>
    static readonly HashSet<string> PlaceholderArtists = new(StringComparer.OrdinalIgnoreCase)
    {
        "various", "various artist", "various artists", "va", "v a",
        "unknown", "unknown artist", "unknown artists", "no artist",
        "soundtrack", "original soundtrack", "ost", "original motion picture soundtrack",
        "none", "n/a", "na"
    };

    /// <summary>True when an artist credit is worth searching or verifying with.</summary>
    public static bool IsUsableArtist(string? artist) =>
        !string.IsNullOrWhiteSpace(artist) && !PlaceholderArtists.Contains(artist.Trim());

    /// <summary>
    /// Ordered provider queries for a track: the cleaned title against each usable artist, then the
    /// raw title (its parentheticals sometimes appear in a provider's title), then the title alone,
    /// and finally free text.
    /// </summary>
    public static IReadOnlyList<LyricsQuery> BuildQueries(string? artist, string? albumArtist, string title)
    {
        string clean = MusicText.CleanTrackTitle(title);
        if (string.IsNullOrWhiteSpace(clean))
            clean = title;

        List<LyricsQuery> queries = [];
        void Add(string? track, string? performer, string? text = null)
        {
            LyricsQuery query = new(track, performer, text);
            if (!queries.Contains(query))
                queries.Add(query);
        }

        IReadOnlyList<string> performers = DistinctArtists(artist, albumArtist);
        foreach (string performer in performers)
        {
            Add(clean, performer);
            if (!string.Equals(clean, title, StringComparison.Ordinal))
                Add(title, performer);
        }

        // The title alone: the artist filter can be wrong even when it is not a placeholder (a
        // soundtrack credited to its composer), so this is always worth one query.
        Add(clean, null);

        string free = performers.Count > 0 ? $"{performers[0]} {clean}".Trim() : clean;
        if (free.Length > 0)
            Add(null, null, free);

        return queries;
    }

    /// <summary>The artists a candidate may be verified against, most specific first. Placeholder
    /// credits are dropped, so a "<c>Various Artists</c>" track is matched on title and duration.</summary>
    public static IReadOnlyList<string> DistinctArtists(string? artist, string? albumArtist)
    {
        List<string> artists = [];
        void Add(string? value)
        {
            if (IsUsableArtist(value) && !artists.Any(a => Same(a, value)))
                artists.Add(value!.Trim());
        }
        Add(artist);
        Add(albumArtist);
        return artists;
    }

    /// <summary>
    /// The best candidate whose artist and title both agree, or null. Durations must match when both
    /// sides know one. This is the only tier that is safe for a multi-artist pop catalogue.
    /// </summary>
    public static LyricsCandidate? PickVerified(
        IEnumerable<LyricsCandidate> candidates,
        IReadOnlyCollection<string> artists,
        string title,
        int durationSeconds)
    {
        return Pick(candidates, title, durationSeconds, (candidate, exact, delta) =>
        {
            if (!ArtistAgrees(artists, candidate.ArtistName))
                return false;
            return delta is null || delta <= (exact ? ExactDurationToleranceSeconds : DurationToleranceSeconds);
        });
    }

    /// <summary>
    /// Markers on a credit that identify a cover, karaoke or re-recording rather than the real
    /// release. The title+duration fallback has no artist signal, so it must at least refuse these;
    /// karaoke versions are often an exact title at an almost identical length.
    /// </summary>
    static readonly string[] CoverMarkers =
        ["karaoke", "tribute", "cover", "instrumental", "made famous by", "originally performed by",
         "in the style of", "as made famous", "backing track", "8-bit", "8 bit", "lullaby", "ringtone"];

    /// <summary>
    /// The fallback tier, for a track whose artist credit is useless (a "Various Artists" placeholder)
    /// or differs from the provider's (a soundtrack credited to its composer). The artist and title
    /// are already known to agree in the weak sense, so the identity rests on the **album** plus a
    /// tight duration: a title alone is not enough, because a common title has many different songs
    /// and one of them will always be a similar length.
    /// </summary>
    public static LyricsCandidate? PickByAlbumAndDuration(
        IEnumerable<LyricsCandidate> candidates,
        string title,
        string album,
        int durationSeconds)
    {
        // No album to corroborate with and no artist to verify against: there is no identity left.
        if (durationSeconds <= 0 || string.IsNullOrWhiteSpace(album))
            return null;

        return Pick(candidates, title, durationSeconds, (candidate, exact, delta) =>
            exact
            && delta is not null
            && delta <= TitleOnlyDurationToleranceSeconds
            && MusicText.TitleMatches(candidate.AlbumName, album)
            && !IsCoverCredit(candidate));
    }

    /// <summary>True when a record's credit marks it as a cover, karaoke or re-recording.</summary>
    static bool IsCoverCredit(LyricsCandidate candidate)
    {
        string name = $"{candidate.ArtistName} {candidate.AlbumName}".ToLowerInvariant();
        return CoverMarkers.Any(marker => name.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>The best of either tier (verified first).</summary>
    public static LyricsCandidate? PickBest(
        IEnumerable<LyricsCandidate> candidates,
        IReadOnlyCollection<string> artists,
        string title,
        string album,
        int durationSeconds)
    {
        List<LyricsCandidate> pool = [.. candidates];
        return PickVerified(pool, artists, title, durationSeconds)
            ?? PickByAlbumAndDuration(pool, title, album, durationSeconds);
    }

    /// <summary>
    /// The lyrics to write for a chosen candidate, or null when there is nothing to write (an
    /// instrumental). Timed lyrics are written as LRC; anything else falls back to plain text. The
    /// matcher does not write word-level timing, so it never advertises a word-sync tier.
    /// </summary>
    public static LyricsContent? ToContent(LyricsCandidate candidate)
    {
        if (candidate.Instrumental)
            return null;

        if (!string.IsNullOrWhiteSpace(candidate.SyncedLyrics))
            return new LyricsContent(candidate.SyncedLyrics.Trim() + Environment.NewLine, LyricsContent.LrcExtension, LyricsTier.Line);

        if (!string.IsNullOrWhiteSpace(candidate.PlainLyrics))
            return new LyricsContent(candidate.PlainLyrics.Trim() + Environment.NewLine, LyricsContent.TextExtension, LyricsTier.Plain);

        return null;
    }

    static LyricsCandidate? Pick(
        IEnumerable<LyricsCandidate> candidates,
        string title,
        int durationSeconds,
        Func<LyricsCandidate, bool, int?, bool> accept)
    {
        string wantedTitle = MusicText.CleanTrackTitle(title);
        if (string.IsNullOrWhiteSpace(wantedTitle))
            wantedTitle = title;
        string wantedKey = MusicText.NormalizeKey(MusicText.CleanReleaseName(wantedTitle));

        LyricsCandidate? best = null;
        int bestScore = int.MinValue;

        foreach (LyricsCandidate candidate in candidates)
        {
            if (!candidate.HasLyrics || !MusicText.TitleMatches(candidate.TrackName, wantedTitle))
                continue;

            bool exact = MusicText.NormalizeKey(MusicText.CleanReleaseName(candidate.TrackName)) == wantedKey;
            int? delta = durationSeconds > 0 && candidate.DurationSeconds > 0
                ? (int)Math.Abs(candidate.DurationSeconds - durationSeconds)
                : null;

            if (!accept(candidate, exact, delta))
                continue;

            int score = 0;
            if (candidate.HasWordSync) score += 100;
            if (!string.IsNullOrWhiteSpace(candidate.SyncedLyrics)) score += 50;
            if (exact) score += 25;
            score -= delta ?? 10;   // closer durations rank higher; unknown is a mild penalty

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    static bool ArtistAgrees(IReadOnlyCollection<string> artists, string candidateArtist)
    {
        // Nothing to verify against: this candidate belongs to the title+duration tier, not this one.
        if (artists.Count == 0)
            return false;

        string candidate = MusicText.NormalizeKey(candidateArtist);
        // An unattributed record must never satisfy the artist check.
        if (candidate.Length == 0)
            return false;

        return artists.Any(a =>
        {
            string wanted = MusicText.NormalizeKey(a);
            return wanted.Length > 0
                && (candidate.Contains(wanted, StringComparison.Ordinal)
                    || wanted.Contains(candidate, StringComparison.Ordinal));
        });
    }

    static bool Same(string? a, string? b) =>
        MusicText.NormalizeKey(a ?? "") is { Length: > 0 } ka && ka == MusicText.NormalizeKey(b ?? "");
}
