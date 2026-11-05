using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.Music.Services;
using Stackarr.Slskd;
using Stackarr.TestSupport;

namespace Stackarr.Music.Tests;

/// <summary>Verifies the Soulseek source contributes grouped candidates to the interactive picker.</summary>
public class SlskdReleaseProviderTests
{
    static (SlskdReleaseProvider Provider, FakeHttpMessageHandler Handler) Build()
    {
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/session")
                ? """{ "token": "t" }"""
                : path.EndsWith("/server")
                    ? """{ "isLoggedIn": true }"""
                    : path.Contains("/searches")
                        ? """
                          { "id": "s1", "isComplete": true, "responses": [
                            { "username": "peer1", "hasFreeUploadSlot": true, "queueLength": 0, "files": [
                              { "filename": "Music\\Ella Langley\\Dandelion\\01 Choosin' Texas.flac", "size": 30000000, "bitRate": 0 },
                              { "filename": "Music\\Ella Langley\\Dandelion\\02 Dandelion.flac", "size": 28000000, "bitRate": 0 } ] } ] }
                          """
                        : "[]";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        SlskdClient client = new("http://slskd:5030", "u", "p", handler);
        SlskdService service = new(
            client,
            new MusicTestHelpers.StaticOptionsMonitor<SlskdConfig>(new SlskdConfig { Enabled = true }),
            new ServiceRegistry(),
            NullLogger<SlskdService>.Instance);
        return (new SlskdReleaseProvider(service, NullLogger<SlskdReleaseProvider>.Instance), handler);
    }

    [Test]
    public async Task Search_GroupsPeerFiles_IntoAnAlbumCandidate()
    {
        (SlskdReleaseProvider provider, _) = Build();
        ReleaseSearchQuery query = new()
        {
            Domain = MusicMediaDomains.Music, Kind = MusicReleaseKinds.Album,
            Artist = "Ella Langley",
            Album = "Dandelion"
        };

        List<ReleaseCandidate> results = [];
        await foreach (ReleaseCandidate release in provider.SearchStreamAsync(query))
            results.Add(release);

        await Assert.That(results).HasCount(1);
        await Assert.That(results[0].Source).IsEqualTo("Soulseek");
        await Assert.That(results[0].Indexer).Contains("peer1");
        await Assert.That(results[0].SourceFiles.Count).IsEqualTo(2);
        await Assert.That(results[0].IsLossless).IsTrue();
    }

    [Test]
    public async Task Search_ExcludesMonoFolders()
    {
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/session")
                ? """{ "token": "t" }"""
                : path.EndsWith("/server")
                    ? """{ "isLoggedIn": true }"""
                    : path.Contains("/searches")
                        ? """
                          { "id": "s1", "isComplete": true, "responses": [
                            { "username": "peer1", "hasFreeUploadSlot": true, "queueLength": 0, "files": [
                              { "filename": "Music\\Ella Langley\\Dandelion (Mono)\\01 Choosin' Texas.flac", "size": 30000000, "bitRate": 0 } ] },
                            { "username": "peer2", "hasFreeUploadSlot": true, "queueLength": 0, "files": [
                              { "filename": "Music\\Ella Langley\\Dandelion\\01 Choosin' Texas.flac", "size": 30000000, "bitRate": 0 } ] } ] }
                          """
                        : "[]";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        SlskdService service = new(
            new SlskdClient("http://slskd:5030", "u", "p", handler),
            new MusicTestHelpers.StaticOptionsMonitor<SlskdConfig>(new SlskdConfig { Enabled = true }),
            new ServiceRegistry(),
            NullLogger<SlskdService>.Instance);
        SlskdReleaseProvider provider = new(service, NullLogger<SlskdReleaseProvider>.Instance);
        ReleaseSearchQuery query = new() { Domain = MusicMediaDomains.Music, Kind = MusicReleaseKinds.Album, Artist = "Ella Langley", Album = "Dandelion" };

        List<ReleaseCandidate> results = [];
        await foreach (ReleaseCandidate release in provider.SearchStreamAsync(query))
            results.Add(release);

        await Assert.That(results).HasCount(1);
        await Assert.That(results[0].Title.Contains("Mono", StringComparison.OrdinalIgnoreCase)).IsFalse();
    }

    [Test]
    public async Task Search_WhenDisabled_ReturnsNothing_AndDoesNotHitNetwork()
    {
        FakeHttpMessageHandler handler = new();
        SlskdClient client = new("http://slskd:5030", "u", "p", handler);
        SlskdService service = new(
            client,
            new MusicTestHelpers.StaticOptionsMonitor<SlskdConfig>(new SlskdConfig { Enabled = false }),
            new ServiceRegistry(),
            NullLogger<SlskdService>.Instance);
        SlskdReleaseProvider provider = new(service, NullLogger<SlskdReleaseProvider>.Instance);

        List<ReleaseCandidate> results = [];
        await foreach (ReleaseCandidate release in provider.SearchStreamAsync(
            new ReleaseSearchQuery { Domain = MusicMediaDomains.Music, Kind = MusicReleaseKinds.Album, Title = "Dandelion" }))
            results.Add(release);

        await Assert.That(results).IsEmpty();
        await Assert.That(handler.Requests).IsEmpty();
    }
}
