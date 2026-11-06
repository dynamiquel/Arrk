using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Playback;
using Arrk.Jellyfin.Playback;
using Arrk.TestSupport;

namespace Arrk.Jellyfin.Tests;

/// <summary>
/// Playback negotiation against the real Jellyfin container started by <see cref="JellyfinServer"/>.
/// The fixture's generated MKV has two audio tracks and two text subtitle tracks, so this proves
/// that an explicit stream choice reaches Jellyfin and that a text subtitle converts to WebVTT.
/// Skipped automatically when Docker or ffmpeg is unavailable.
/// </summary>
[NotInParallel(JellyfinServerShared.ParallelKey)]
public class JellyfinPlaybackIntegrationTests
{
    static Task<JellyfinServer?> ServerAsync() => JellyfinServerShared.GetServerAsync();

    [Test]
    public async Task StartAsync_SelectsTracksAndServesWebVtt()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null || !server.HasMedia)
            return;

        JellyfinClient client = server.CreateClient();
        JellyfinItemsResult items = await server.WaitForItemsAsync("Movie", TimeSpan.FromMinutes(3));
        JellyfinItem? movie = items.Items.FirstOrDefault();
        if (movie is null)
            return;

        JellyfinItem? item = await client.GetItemAsync(movie.Id);
        await Assert.That(item).IsNotNull();
        await Assert.That(item!.MediaSources.Count).IsGreaterThan(0);

        List<JellyfinMediaStream> streams = item.MediaSources[0].MediaStreams;
        List<JellyfinMediaStream> audios = streams
            .Where(s => string.Equals(s.Type, "Audio", StringComparison.OrdinalIgnoreCase))
            .ToList();
        List<JellyfinMediaStream> subtitles = streams
            .Where(s => string.Equals(s.Type, "Subtitle", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(s.Codec, "pgs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // HasMedia is true only when ffmpeg generated the multi-track sample, so missing tracks
        // are a real failure rather than a reason to soft-skip.
        await Assert.That(audios.Count).IsGreaterThan(1);
        await Assert.That(subtitles.Count).IsGreaterThan(0);

        int audioIndex = audios[1].Index;
        int subtitleIndex = subtitles[0].Index;

        JellyfinPlaybackService service = new(
            client,
            new PlaybackSessionStore(),
            new StreamTokenService(new byte[32]),
            server.CreateLinkService(),
            new MutableOptionsMonitor<JellyfinConfig>(new JellyfinConfig()),
            NullLogger<JellyfinPlaybackService>.Instance);

        PlaybackPlan? plan = await service.StartAsync(
            JellyfinServer.AdminUsername,
            movie.Id,
            new BrowserCapabilities(),
            startTicks: 1_000_000,
            audioStreamIndex: audioIndex,
            subtitleStreamIndex: subtitleIndex,
            default);

        await Assert.That(plan).IsNotNull();
        await Assert.That(plan!.MediaSourceId).IsEqualTo(item.MediaSources[0].Id);
        await Assert.That(plan.AudioTracks.Count).IsGreaterThan(1);
        await Assert.That(plan.SelectedAudioIndex).IsEqualTo(audioIndex);
        await Assert.That(plan.SelectedSubtitleIndex).IsEqualTo(subtitleIndex);
        await Assert.That(plan.SubtitlesBurnedIn).IsFalse();

        PlaybackSubtitle subtitle = plan.Subtitles.Single(s => s.Index == subtitleIndex);
        await Assert.That(subtitle.IsText).IsTrue();
        await Assert.That(subtitle.Url).IsNotNull();

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
}
