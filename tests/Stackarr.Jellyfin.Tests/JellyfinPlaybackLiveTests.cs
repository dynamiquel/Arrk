using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core.Configuration;
using Stackarr.Core.Playback;
using Stackarr.Jellyfin.Playback;
using Stackarr.TestSupport;

namespace Stackarr.Jellyfin.Tests;

/// <summary>
/// Optional end-to-end smoke test against a real Jellyfin library with multi-track media. It runs
/// only when <c>STACKARR_LIVE_JELLYFIN_URL</c> is set (and media with more than one audio track is
/// present), so the normal suite stays hermetic. This is the test that proves Jellyfin honours our
/// explicit stream selection and that a text subtitle really converts to WebVTT.
/// </summary>
public class JellyfinPlaybackLiveTests
{
    static readonly string[] ImageCodecs =
        ["pgs", "pgssub", "hdmv_pgs_subtitle", "dvdsub", "dvd_subtitle", "dvbsub", "dvb_subtitle", "vobsub", "xsub"];

    [Test]
    public async Task StartAsync_LiveLibrary_SelectsTracksAndServesWebVtt()
    {
        string? url = Environment.GetEnvironmentVariable("STACKARR_LIVE_JELLYFIN_URL");
        if (string.IsNullOrWhiteSpace(url))
            return;

        string username = Environment.GetEnvironmentVariable("STACKARR_LIVE_JELLYFIN_USER") ?? "admin";
        string password = Environment.GetEnvironmentVariable("STACKARR_LIVE_JELLYFIN_PASSWORD") ?? "admin";

        JellyfinClient client = new(url, username, password, "stackarr-live-playback-test");
        JellyfinItemsResult items = await client.GetItemsAsync(new JellyfinItemQuery
        {
            Recursive = true,
            IncludeItemTypes = "Episode,Movie",
            Limit = 50,
            Fields = ["MediaSources"]
        });

        JellyfinItem? item = items.Items
            .Where(i => i.MediaSources.Count > 0)
            .FirstOrDefault(i =>
            {
                List<JellyfinMediaStream> streams = i.MediaSources[0].MediaStreams;
                return streams.Count(s => string.Equals(s.Type, "Audio", StringComparison.OrdinalIgnoreCase)) > 1
                    && streams.Any(s => IsTextSubtitle(s));
            });

        if (item is null)
            return;

        List<JellyfinMediaStream> mediaStreams = item.MediaSources[0].MediaStreams;
        int audioIndex = mediaStreams.Where(s => string.Equals(s.Type, "Audio", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Index)
            .Last();
        int subtitleIndex = mediaStreams.First(s => IsTextSubtitle(s)).Index;

        JellyfinPlaybackService service = new(
            client,
            new PlaybackSessionStore(),
            new StreamTokenService(new byte[32]),
            new JellyfinLinkService(
                client,
                new MutableOptionsMonitor<JellyfinConfig>(new JellyfinConfig()),
                new MutableOptionsMonitor<RoutesConfig>(new RoutesConfig()),
                NullLogger<JellyfinLinkService>.Instance),
            new MutableOptionsMonitor<JellyfinConfig>(new JellyfinConfig()),
            NullLogger<JellyfinPlaybackService>.Instance);

        PlaybackPlan? plan = await service.StartAsync(
            username,
            item.Id,
            new BrowserCapabilities(),
            startTicks: 10_000_000,
            audioStreamIndex: audioIndex,
            subtitleStreamIndex: subtitleIndex,
            default);

        await Assert.That(plan).IsNotNull();
        await Assert.That(plan!.AudioTracks.Count).IsGreaterThan(1);
        await Assert.That(plan.SelectedAudioIndex).IsEqualTo(audioIndex);
        await Assert.That(plan.SelectedSubtitleIndex).IsEqualTo(subtitleIndex);
        await Assert.That(plan.SubtitlesBurnedIn).IsFalse();
        await Assert.That(plan.StreamUrl).Contains("/api/media/player/stream/");

        PlaybackSubtitle subtitle = plan.Subtitles.Single(s => s.Index == subtitleIndex);
        await Assert.That(subtitle.IsText).IsTrue();
        await Assert.That(subtitle.Url).IsNotNull();

        // Follow the proxy URL back to the Jellyfin path it wraps and prove the conversion works.
        int pathStart = subtitle.Url!.IndexOf("/Videos/", StringComparison.Ordinal);
        await Assert.That(pathStart).IsGreaterThanOrEqualTo(0);

        using HttpResponseMessage response = await client.SendProxyAsync(
            HttpMethod.Get,
            subtitle.Url[pathStart..],
            rangeHeader: null,
            default);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string vtt = await response.Content.ReadAsStringAsync();
        await Assert.That(vtt.StartsWith("WEBVTT", StringComparison.Ordinal)).IsTrue();
    }

    static bool IsTextSubtitle(JellyfinMediaStream stream) =>
        string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase)
        && !ImageCodecs.Contains(stream.Codec ?? "", StringComparer.OrdinalIgnoreCase);
}
