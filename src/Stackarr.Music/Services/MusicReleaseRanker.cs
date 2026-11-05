using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Ai;
using Stackarr.Ai.Data;
using Stackarr.Core.Models;

namespace Stackarr.Music.Services;

/// <summary>The model's JSON answer. <see cref="Pick"/> is 1-based; 0 means abstain.</summary>
public sealed class MusicRankAnswer
{
    public int Pick { get; set; }
    public double Confidence { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>One Soulseek file that already passed the deterministic filename/artist/version filters.</summary>
public sealed record MusicTrackCandidate(
    string Peer,
    string FileName,
    long Size,
    int BitRate,
    int? SampleRate,
    int? BitDepth,
    int? LengthSeconds,
    bool HasFreeUploadSlot,
    int QueueLength,
    int Score,
    bool IsLossless)
{
    public string LeafName => Path.GetFileName(FileName.Replace('\\', '/'));

    public string QualityLabel => IsLossless
        ? "lossless"
        : BitRate > 0 ? $"{BitRate} kbps" : "bitrate unknown";

    public string LengthLabel => LengthSeconds is > 0
        ? $"{LengthSeconds.Value / 60}:{LengthSeconds.Value % 60:00}"
        : "unknown";
}

/// <summary>
/// Optional AI tie-breaker for music grabs. It sits <b>after</b> the deterministic search/filter/sort
/// and only ever chooses between candidates that already passed it, the existing scoring, junk
/// filters, artist rules and import verification stay authoritative.
///
/// Modes (see <see cref="AiReleaseRankingFeature"/>):
/// <list type="bullet">
/// <item><c>shadow</c> calls the model, records what it would have picked and changes nothing.</item>
/// <item><c>ambiguous</c> acts only when the top candidates are effectively tied or edition markers
/// are present.</item>
/// <item><c>always</c> lets the model choose among the top candidates.</item>
/// </list>
/// Any failure, abstention, low confidence or unknown pick returns <c>null</c>, and the caller keeps
/// the deterministic choice.
/// </summary>
public sealed class MusicReleaseRanker(
    AiService ai,
    AiDecisionService decisions,
    MusicLibrary library,
    IOptionsMonitor<AiConfig> config,
    ILogger<MusicReleaseRanker> logger)
{
    public const string DecisionFeature = AiFeatureKeys.ReleaseRanking;
    public const string DecisionOperation = "music.releaseRank";
    public const string TrackDecisionOperation = "music.trackRank";

    static readonly string[] EditionMarkers =
    [
        "remaster", "remastered", "deluxe", "expanded", "anniversary", "special edition",
        "bonus", "ost", "soundtrack", "live", "compilation", "single", "ep", "mono", "stereo"
    ];

    /// <summary>
    /// Returns the model's pick, or <c>null</c> to keep the deterministic choice. Never throws.
    /// </summary>
    public async Task<MusicRelease?> PickAsync(
        int albumId,
        string artist,
        string title,
        bool isTrack,
        IReadOnlyList<MusicRelease> candidates,
        CancellationToken ct = default)
    {
        AiConfig current = config.CurrentValue;
        AiReleaseRankingFeature feature = current.Features.ReleaseRanking;

        if (!ai.Enabled || !feature.Enabled
            || string.Equals(feature.Mode, AiRankingModes.Off, StringComparison.OrdinalIgnoreCase)
            || candidates.Count < 2)
            return null;

        List<MusicRelease> top = candidates
            .Take(Math.Clamp(feature.MaxCandidates, 2, 50))
            .ToList();

        if (string.Equals(feature.Mode, AiRankingModes.Ambiguous, StringComparison.OrdinalIgnoreCase)
            && !IsAmbiguous(top, feature.AmbiguityScoreWindow))
            return null;

        MusicRelease deterministic = candidates[0];
        string prompt;
        try
        {
            prompt = await BuildPromptAsync(albumId, artist, title, isTrack, top, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not build the music ranking prompt for \"{Title}\"", title);
            return null;
        }

        MusicRankAnswer? answer = null;
        try
        {
            answer = await ai.AskJsonAsync<MusicRankAnswer>(
                prompt,
                AiModelRole.Fast,
                DecisionFeature,
                DecisionOperation,
                userId: null,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI music ranking failed for \"{Title}\"; keeping the deterministic pick", title);
            return null;
        }

        if (answer is null || answer.Pick <= 0 || answer.Pick > top.Count)
        {
            logger.LogDebug("AI music ranking abstained for \"{Title}\"", title);
            return null;
        }

        MusicRelease picked = top[answer.Pick - 1];
        bool applied = !string.Equals(feature.Mode, AiRankingModes.Shadow, StringComparison.OrdinalIgnoreCase)
            && answer.Confidence >= feature.MinConfidence;

        await RecordDecisionAsync(albumId, artist, title, feature.Mode, deterministic, picked,
            answer, appendedContext: prompt, applied: applied, ct: ct);

        if (!applied)
        {
            logger.LogInformation(
                "AI music ranking for \"{Title}\" picked \"{Pick}\" ({Confidence:0.00}); mode {Mode} kept \"{Deterministic}\"",
                title, picked.Title, answer.Confidence, feature.Mode, deterministic.Title);
            return null;
        }

        logger.LogInformation(
            "AI music ranking for \"{Title}\" chose \"{Pick}\" over \"{Deterministic}\" ({Confidence:0.00}): {Reason}",
            title, picked.Title, deterministic.Title, answer.Confidence, answer.Reason);

        return picked;
    }

    /// <summary>
    /// Ranks individual Soulseek files for a song request. Same safety rails as the album ranker:
    /// only candidates that already passed the filename/artist/version filters are considered, and
    /// any failure/abstention keeps the deterministic best file.
    /// </summary>
    public async Task<MusicTrackCandidate?> PickTrackAsync(
        int albumId,
        string artist,
        string title,
        long deezerTrackId,
        IReadOnlyList<MusicTrackCandidate> candidates,
        CancellationToken ct = default)
    {
        AiConfig current = config.CurrentValue;
        AiReleaseRankingFeature feature = current.Features.ReleaseRanking;

        if (!ai.Enabled || !feature.Enabled
            || string.Equals(feature.Mode, AiRankingModes.Off, StringComparison.OrdinalIgnoreCase)
            || candidates.Count < 2)
            return null;

        List<MusicTrackCandidate> top = candidates
            .Take(Math.Clamp(feature.MaxCandidates, 2, 50))
            .ToList();

        if (string.Equals(feature.Mode, AiRankingModes.Ambiguous, StringComparison.OrdinalIgnoreCase)
            && !IsAmbiguousTrack(top, feature.AmbiguityScoreWindow))
            return null;

        MusicTrackCandidate deterministic = candidates[0];
        string prompt;
        try
        {
            prompt = await BuildTrackPromptAsync(albumId, artist, title, deezerTrackId, top, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not build the track ranking prompt for \"{Title}\"", title);
            return null;
        }

        MusicRankAnswer? answer;
        try
        {
            answer = await ai.AskJsonAsync<MusicRankAnswer>(
                prompt,
                AiModelRole.Fast,
                DecisionFeature,
                TrackDecisionOperation,
                userId: null,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI track ranking failed for \"{Title}\"; keeping the deterministic file", title);
            return null;
        }

        if (answer is null || answer.Pick <= 0 || answer.Pick > top.Count)
        {
            logger.LogDebug("AI track ranking abstained for \"{Title}\"", title);
            return null;
        }

        MusicTrackCandidate picked = top[answer.Pick - 1];
        bool applied = !string.Equals(feature.Mode, AiRankingModes.Shadow, StringComparison.OrdinalIgnoreCase)
            && answer.Confidence >= feature.MinConfidence;

        await RecordTrackDecisionAsync(albumId, artist, title, deezerTrackId, feature.Mode,
            deterministic, picked, answer, prompt, applied, ct);

        if (!applied)
        {
            logger.LogInformation(
                "AI track ranking for \"{Title}\" picked \"{Pick}\" ({Confidence:0.00}); mode {Mode} kept \"{Deterministic}\"",
                title, picked.LeafName, answer.Confidence, feature.Mode, deterministic.LeafName);
            return null;
        }

        logger.LogInformation(
            "AI track ranking for \"{Title}\" chose \"{Pick}\" over \"{Deterministic}\" ({Confidence:0.00}): {Reason}",
            title, picked.LeafName, deterministic.LeafName, answer.Confidence, answer.Reason);

        return picked;
    }

    /// <summary>Best-effort outcome link used by the importer (imported/mismatch/failed).</summary>
    public Task ResolveOutcomeAsync(int albumId, string releaseTitle, string outcome, CancellationToken ct = default)
    {
        if (albumId <= 0 || string.IsNullOrWhiteSpace(releaseTitle))
            return Task.CompletedTask;

        return decisions.ResolveAsync(
            DecisionFeature,
            albumId.ToString(CultureInfo.InvariantCulture),
            releaseTitle,
            outcome,
            ct);
    }

    /// <summary>Best-effort outcome link for a single-track Soulseek grab.</summary>
    public Task ResolveTrackOutcomeAsync(
        long deezerTrackId,
        string fileName,
        string outcome,
        CancellationToken ct = default)
    {
        if (deezerTrackId <= 0 || string.IsNullOrWhiteSpace(fileName))
            return Task.CompletedTask;

        string leaf = Path.GetFileName(fileName.Replace('\\', '/'));
        return decisions.ResolveAsync(
            DecisionFeature,
            $"track:{deezerTrackId.ToString(CultureInfo.InvariantCulture)}",
            leaf,
            outcome,
            ct);
    }

    async Task RecordTrackDecisionAsync(
        int albumId,
        string artist,
        string title,
        long deezerTrackId,
        string mode,
        MusicTrackCandidate deterministic,
        MusicTrackCandidate picked,
        MusicRankAnswer answer,
        string appendedContext,
        bool applied,
        CancellationToken ct)
    {
        try
        {
            string subject = deezerTrackId > 0
                ? $"track:{deezerTrackId.ToString(CultureInfo.InvariantCulture)}"
                : albumId > 0
                    ? $"album:{albumId.ToString(CultureInfo.InvariantCulture)}|{title}"
                    : $"{artist}|{title}";

            await decisions.RecordAsync(new AiDecisionOutcome
            {
                Feature = DecisionFeature,
                Operation = TrackDecisionOperation,
                Subject = Truncate(subject, 128),
                Request = Truncate($"{artist} — {title}", 512),
                Context = Truncate(appendedContext, 8000),
                Mode = mode,
                DeterministicChoice = Truncate(deterministic.LeafName, 512),
                AiChoice = Truncate(picked.LeafName, 512),
                Confidence = answer.Confidence,
                Reason = Truncate(answer.Reason, 500),
                Applied = applied
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not record the AI track decision for \"{Title}\"", title);
        }
    }

    async Task RecordDecisionAsync(
        int albumId,
        string artist,
        string title,
        string mode,
        MusicRelease deterministic,
        MusicRelease picked,
        MusicRankAnswer answer,
        string appendedContext,
        bool applied,
        CancellationToken ct)
    {
        try
        {
            await decisions.RecordAsync(new AiDecisionOutcome
            {
                Feature = DecisionFeature,
                Operation = DecisionOperation,
                Subject = albumId > 0
                    ? albumId.ToString(CultureInfo.InvariantCulture)
                    : $"{artist}|{title}",
                Request = Truncate($"{artist} — {title}", 512),
                Context = Truncate(appendedContext, 8000),
                Mode = mode,
                DeterministicChoice = deterministic.Title,
                AiChoice = picked.Title,
                Confidence = answer.Confidence,
                Reason = Truncate(answer.Reason, 500),
                Applied = applied
            }, ct);
        }
        catch (Exception ex)
        {
            // Decision telemetry must never affect acquisition.
            logger.LogDebug(ex, "Could not record the AI music decision for \"{Title}\"", title);
        }
    }

    async Task<string> BuildPromptAsync(
        int albumId,
        string artist,
        string title,
        bool isTrack,
        IReadOnlyList<MusicRelease> candidates,
        CancellationToken ct)
    {
        StringBuilder prompt = new();
        prompt.AppendLine("You are choosing which torrent release to download for a music library.");
        prompt.AppendLine("The candidates below already passed a deterministic title/artist matcher; pick the one most likely to be the exact release requested.");
        prompt.AppendLine();

        if (albumId > 0)
        {
            Album? album = await library.GetAlbumAsync(albumId, ct);
            if (album is not null)
            {
                prompt.AppendLine(isTrack
                    ? $"Target: track \"{album.Title}\" by \"{album.ArtistName}\" (from the album)."
                    : $"Target: album \"{album.Title}\" by \"{album.ArtistName}\".");
                prompt.AppendLine($"Album type: {Value(album.AlbumType)}; year: {(album.ReleaseDate?.Year.ToString(CultureInfo.InvariantCulture) ?? "unknown")}; tracks: {album.TotalTrackCount}.");

                List<string> trackTitles = album.Tracks
                    .Select(t => t.Title.Trim())
                    .Where(t => t.Length > 0)
                    .Take(20)
                    .ToList();
                if (trackTitles.Count > 0)
                    prompt.AppendLine($"Known track titles: {string.Join("; ", trackTitles)}");

                prompt.AppendLine();
            }
        }

        if (!prompt.ToString().Contains("Target:", StringComparison.Ordinal))
            prompt.AppendLine($"Target: {(isTrack ? "track" : "album")} \"{title}\" by \"{artist}\".");

        prompt.AppendLine("Candidates:");
        for (int i = 0; i < candidates.Count; i++)
        {
            MusicRelease candidate = candidates[i];
            prompt.AppendLine(
                $"{i + 1}. \"{Value(candidate.Title)}\" | indexer {Value(candidate.Indexer)} | {Value(candidate.SizeLabel)} | {candidate.Seeders} seeders | {(candidate.IsLossless ? "lossless" : "lossy/unknown")} | match score {candidate.Score}");
        }

        prompt.AppendLine();
        prompt.AppendLine("Rules:");
        prompt.AppendLine("- Prefer the complete album/edition that matches the target artist, title and track list.");
        prompt.AppendLine("- Avoid singles, compilations, live albums, covers, karaoke, tributes and other artists when the target is a studio album.");
        prompt.AppendLine("- Prefer a release whose title suggests the same edition/remaster; a different edition is still better than a different album.");
        prompt.AppendLine("- If no candidate is clearly the requested release, abstain.");
        prompt.AppendLine();
        prompt.AppendLine("Return JSON only, no markdown:");
        prompt.AppendLine("{\"pick\": <candidate number, or 0 to abstain>, \"confidence\": <0.0-1.0>, \"reason\": \"<one short sentence>\"}");

        return prompt.ToString();
    }

    async Task<string> BuildTrackPromptAsync(
        int albumId,
        string artist,
        string title,
        long deezerTrackId,
        IReadOnlyList<MusicTrackCandidate> candidates,
        CancellationToken ct)
    {
        StringBuilder prompt = new();
        prompt.AppendLine("You are choosing which Soulseek file to download for a specific song.");
        prompt.AppendLine("Every candidate below already passed a deterministic filename/artist matcher and version filter.");
        prompt.AppendLine();
        prompt.AppendLine($"Target: track \"{Value(title)}\" by \"{Value(artist)}\".");

        if (albumId > 0)
        {
            Album? album = await library.GetAlbumAsync(albumId, ct);
            if (album is not null)
            {
                prompt.AppendLine($"Album: \"{Value(album.Title)}\" ({Value(album.AlbumType)}, {(album.ReleaseDate?.Year.ToString(CultureInfo.InvariantCulture) ?? "unknown")}).");

                Track? expected = deezerTrackId > 0
                    ? album.Tracks.FirstOrDefault(t => t.DeezerId == deezerTrackId)
                    : album.Tracks.FirstOrDefault(t => string.Equals(t.Title, title, StringComparison.OrdinalIgnoreCase));
                if (expected is not null && expected.DurationSeconds > 0)
                    prompt.AppendLine($"Expected track duration: {expected.DurationSeconds}s.");
            }
        }

        prompt.AppendLine("Candidates:");
        for (int i = 0; i < candidates.Count; i++)
        {
            MusicTrackCandidate c = candidates[i];
            prompt.AppendLine(
                $"{i + 1}. peer {Value(c.Peer)} | \"{Value(c.LeafName)}\" | {Value(c.QualityLabel)} | {(c.SampleRate is > 0 ? $"{c.SampleRate} Hz" : "sample rate unknown")} | {(c.BitDepth is > 0 ? $"{c.BitDepth}-bit" : "bit depth unknown")} | {c.LengthLabel} | {(c.HasFreeUploadSlot ? "free slot" : "queued")} | queue {c.QueueLength}");
        }

        prompt.AppendLine();
        prompt.AppendLine("Rules:");
        prompt.AppendLine("- Pick the file most likely to be the exact requested song by this artist.");
        prompt.AppendLine("- Avoid edits, remixes, live versions, covers, instrumentals, karaoke and tributes unless the title itself asks for one.");
        prompt.AppendLine("- Prefer lossless or higher bitrate when the file is otherwise the same song and duration.");
        prompt.AppendLine("- Prefer a shorter queue / free upload slot when quality is comparable.");
        prompt.AppendLine("- If no candidate is clearly the requested song, abstain.");
        prompt.AppendLine();
        prompt.AppendLine("Return JSON only, no markdown:");
        prompt.AppendLine("{\"pick\": <candidate number, or 0 to abstain>, \"confidence\": <0.0-1.0>, \"reason\": \"<one short sentence>\"}");

        return prompt.ToString();
    }

    static bool IsAmbiguousTrack(IReadOnlyList<MusicTrackCandidate> top, int scoreWindow)
    {
        if (top.Count < 2)
            return false;

        if (top[0].Score - top[1].Score <= scoreWindow)
            return true;

        // Two lossless files (or two with the same bitrate) are a quality tie even when the small
        // slot/queue bonuses make their scores differ.
        if (top[0].IsLossless && top[1].IsLossless)
            return true;

        if ((top[0].BitRate > 0 && top[0].BitRate == top[1].BitRate)
            && top[0].IsLossless == top[1].IsLossless)
            return true;

        return false;
    }

    static bool IsAmbiguous(IReadOnlyList<MusicRelease> top, int scoreWindow)
    {
        if (top.Count < 2)
            return false;

        if (top[0].Score - top[1].Score <= scoreWindow)
            return true;

        int inspect = Math.Min(top.Count, 5);
        for (int i = 0; i < inspect; i++)
        {
            if (HasEditionMarker(top[i].Title))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Token-aware marker check so short markers ("ep", "ost", "live") cannot match inside ordinary
    /// words such as "Deep", "Lost" or "Oliver".
    /// </summary>
    static bool HasEditionMarker(string title)
    {
        string normalized = " " + new string(title
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray()) + " ";
        normalized = string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        normalized = $" {normalized} ";

        foreach (string marker in EditionMarkers)
        {
            string needle = $" {marker} ";
            if (normalized.Contains(needle, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    static string Value(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : Truncate(value.ReplaceLineEndings(" "), 200);

    static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];
}
