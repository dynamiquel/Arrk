using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Playback;
using Arrk.Jellyfin;
using Arrk.Jellyfin.Playback;
using Arrk.Navidrome;

namespace Arrk.App.Tests;

/// <summary>
/// Covers AirPlay stream authentication. AirPlay hands the media URL to the receiver, which fetches
/// it with none of the browser's cookies, so both stream proxies must accept a signed token without
/// an auth cookie and reject a missing, tampered or foreign one. The upstream service is unreachable
/// on purpose; reaching it (a 502 rather than a 401) shows the token gate passed.
/// </summary>
public class AirPlayStreamAuthTests
{
    sealed class StreamHost : IAsyncDisposable
    {
        StreamHost(WebApplication app, HttpClient client, StreamTokenService tokens, PlaybackSessionStore sessions)
        {
            App = app;
            Client = client;
            Tokens = tokens;
            Sessions = sessions;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }
        public StreamTokenService Tokens { get; }
        public PlaybackSessionStore Sessions { get; }

        public static async Task<StreamHost> CreateAsync()
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            StreamTokenService tokens = new(RandomNumberGenerator.GetBytes(32));
            PlaybackSessionStore sessions = new();

            builder.Services.AddSingleton(tokens);
            builder.Services.AddSingleton(sessions);
            builder.Services.AddSingleton(new JellyfinClient("http://127.0.0.1:1/jellyfin", "admin", "admin", "test"));
            builder.Services.AddSingleton(new NavidromeClient("http://127.0.0.1:1/", "arrk", "secret", new HttpClientHandler()));

            // Minimal API parameter inference needs every service the mapped endpoints declare, even
            // the ones the stream routes never touch (start/stop/scrobble). The heavy ones are inert.
            builder.Services.AddOptions<JellyfinConfig>();
            builder.Services.AddOptions<RoutesConfig>();
            builder.Services.AddSingleton(sp => new JellyfinLinkService(
                sp.GetRequiredService<JellyfinClient>(),
                sp.GetRequiredService<IOptionsMonitor<JellyfinConfig>>(),
                sp.GetRequiredService<IOptionsMonitor<RoutesConfig>>(),
                NullLogger<JellyfinLinkService>.Instance));
            builder.Services.AddSingleton(sp => new JellyfinPlaybackService(
                sp.GetRequiredService<JellyfinClient>(),
                sp.GetRequiredService<PlaybackSessionStore>(),
                sp.GetRequiredService<StreamTokenService>(),
                sp.GetRequiredService<JellyfinLinkService>(),
                sp.GetRequiredService<IOptionsMonitor<JellyfinConfig>>(),
                NullLogger<JellyfinPlaybackService>.Instance));
            builder.Services.AddSingleton(new MusicLibraryService(null!, null!, NullLogger<MusicLibraryService>.Instance));
            // Endpoint parameter inference resolves every declared service even when the stream
            // routes never execute them; the activity recorder is inert in this host.
            builder.Services.AddSingleton(new WatchActivityService(new ThrowingDbContextFactory(), TimeProvider.System));
            builder.Services.AddSingleton<JellyfinWatchActivityRecorder>();
            // The lyrics route declares the app-facing Navidrome wrapper, so the host must be able to
            // resolve it even though the stream routes never touch it.
            builder.Services.AddSingleton(new NavidromeApiSecret { Username = "arrk", Password = "secret" });
            builder.Services.AddOptions<NavidromeConfig>();
            builder.Services.AddSingleton(sp => new NavidromeService(
                sp.GetRequiredService<NavidromeClient>(),
                sp.GetRequiredService<NavidromeApiSecret>(),
                sp.GetRequiredService<IOptionsMonitor<NavidromeConfig>>(),
                NullLogger<NavidromeService>.Instance));

            WebApplication app = builder.Build();
            app.MapJellyfinPlaybackEndpoints();
            app.MapNavidromePlaybackEndpoints();
            await app.StartAsync();

            TestServer server = (TestServer)app.Services.GetRequiredService<IServer>();
            return new StreamHost(app, server.CreateClient(), tokens, sessions);
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    sealed class ThrowingDbContextFactory : IDbContextFactory<ArrkDbContext>
    {
        public ArrkDbContext CreateDbContext() => throw new InvalidOperationException("Not used by the stream tests.");

        public Task<ArrkDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not used by the stream tests.");
    }

    [Test]
    public async Task VideoStream_WithValidTokenAndNoCookie_ReachesTheProxy()
    {
        await using StreamHost host = await StreamHost.CreateAsync();
        PlaybackSession session = host.Sessions.Create("alice", "item1", "ms1", "ps1");
        string token = host.Tokens.Create(
            JellyfinPlaybackService.MediaStreamScope, session.SessionId, TimeSpan.FromHours(1));

        // No cookie is sent (that is exactly what an Apple TV does); a 502 means the token gate
        // passed and only the unreachable upstream failed.
        HttpResponseMessage response = await host.Client.GetAsync(
            $"/api/media/player/stream/{session.SessionId}/{token}/Videos/item1/master.m3u8");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
    }

    [Test]
    public async Task VideoStream_WithoutValidToken_IsRejected()
    {
        await using StreamHost host = await StreamHost.CreateAsync();
        PlaybackSession session = host.Sessions.Create("alice", "item1", "ms1", "ps1");

        HttpResponseMessage response = await host.Client.GetAsync(
            $"/api/media/player/stream/{session.SessionId}/not-a-real-token/Videos/item1/master.m3u8");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task VideoStream_WithTokenForAnotherSession_IsRejected()
    {
        await using StreamHost host = await StreamHost.CreateAsync();
        PlaybackSession alice = host.Sessions.Create("alice", "item1", "ms1", "ps1");
        PlaybackSession bob = host.Sessions.Create("bob", "item2", "ms2", "ps2");
        string bobToken = host.Tokens.Create(
            JellyfinPlaybackService.MediaStreamScope, bob.SessionId, TimeSpan.FromHours(1));

        HttpResponseMessage response = await host.Client.GetAsync(
            $"/api/media/player/stream/{alice.SessionId}/{bobToken}/Videos/item1/master.m3u8");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task MusicStream_WithValidTokenAndNoCookie_ReachesTheProxy()
    {
        await using StreamHost host = await StreamHost.CreateAsync();
        string token = host.Tokens.Create(
            NavidromePlaybackEndpoints.MusicStreamScope, "alice", TimeSpan.FromHours(1));

        HttpResponseMessage response = await host.Client.GetAsync(
            $"/api/music/player/stream/song1?{NavidromePlaybackEndpoints.StreamTokenQuery}={Uri.EscapeDataString(token)}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
    }

    [Test]
    public async Task MusicStream_WithoutToken_IsRejected()
    {
        await using StreamHost host = await StreamHost.CreateAsync();

        HttpResponseMessage response = await host.Client.GetAsync("/api/music/player/stream/song1");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }
}
