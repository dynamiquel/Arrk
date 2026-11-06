using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Auth.Abstractions;
using Arrk.Core.Playback;

namespace Arrk.Jellyfin;

/// <summary>
/// Polls Jellyfin sessions so plays from external clients (TV apps, mobile, Infuse, Jellyfin Web)
/// reach Arrk's history with device and client names. The in-site player records directly via
/// <see cref="JellyfinWatchActivityRecorder"/>; both use the same item identity.
/// </summary>
public sealed class JellyfinWatchActivityMonitor(
    JellyfinClient client,
    JellyfinWatchActivityRecorder recorder,
    IOptionsMonitor<JellyfinConfig> config,
    IServiceScopeFactory scopeFactory,
    ILogger<JellyfinWatchActivityMonitor> logger) : BackgroundService
{
    bool _backfilled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the host and Jellyfin a moment to settle before the first poll.
        await DelayAsync(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                JellyfinConfig current = config.CurrentValue;
                if (!current.Enabled || !current.ActivityTracking)
                {
                    await DelayAsync(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                if (!_backfilled)
                {
                    await BackfillAsync(stoppingToken);
                    _backfilled = true;
                }

                await PollSessionsAsync(stoppingToken);
                int seconds = Math.Clamp(current.ActivityPollSeconds, 5, 300);
                await DelayAsync(TimeSpan.FromSeconds(seconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Jellyfin watch-activity poll failed");
                await DelayAsync(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    async Task PollSessionsAsync(CancellationToken ct)
    {
        List<JellyfinSession> sessions = await client.GetSessionsAsync(activeWithinSeconds: 600, ct);
        foreach (JellyfinSession session in sessions)
        {
            JellyfinItem? item = session.NowPlayingItem;
            if (item is null || string.IsNullOrWhiteSpace(session.UserName))
                continue;

            long position = session.PlayState?.PositionTicks ?? 0;
            await recorder.RecordAsync(
                session.UserName,
                item,
                WatchActivitySources.Jellyfin,
                device: session.DeviceName,
                clientName: session.Client,
                ipAddress: session.RemoteEndPoint,
                sessionKey: $"jellyfin:{session.Id}:{item.Id}",
                positionTicks: position,
                completed: false,
                ct: ct);
        }
    }

    async Task BackfillAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        IUserDirectory users = scope.ServiceProvider.GetRequiredService<IUserDirectory>();
        foreach (ArrkUser user in await users.GetAllAsync(ct))
        {
            try
            {
                await recorder.BackfillAsync(user.Username, ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Jellyfin history backfill failed for {User}", user.Username);
            }
        }
    }

    static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            /* shutdown */
        }
    }
}
