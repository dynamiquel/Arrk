using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Auth.Abstractions;
using Arrk.Core.Configuration;
using Arrk.Core.Documentation;
using Arrk.Core.Modules;
using Arrk.Core.Playback;
using Arrk.Core.Services;
using Arrk.Jellyfin.Jobs;
using Arrk.Jellyfin.Playback;
using Arrk.Ui;

namespace Arrk.Jellyfin;

public class JellyfinModule : IArrkModule
{
    public const int HttpPort = 8096;

    public string ModuleName => "Jellyfin";
    public string ModuleDescription => "Jellyfin provisioning, SSO and the in-site web player.";

    public IReadOnlyList<Service> GetServices()
    {
        return [new(new JellyfinServiceDefinition())];
    }

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
        [new ConfigSectionProvider<JellyfinConfig>("services:jellyfin", "Jellyfin",
            icon: "PlayCircle", group: "Services", order: 60, groupOrder: 20)];

    public IReadOnlyList<ModuleDependency> GetDependencies() =>
    [
        new("Jellyfin", "GPL-2.0", "Jellyfin", "https://github.com/jellyfin/jellyfin", "Free software media system and media server"),
        new("hls.js", "Apache-2.0", "hls.js contributors", "https://github.com/video-dev/hls.js", "HLS playback in Chromium/Firefox for the in-site web player"),
    ];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDocumentation("jellyfin", typeof(JellyfinModule).Assembly);
        services.ConfigureConfig<JellyfinConfig>(configuration.GetSection("services:jellyfin"));

        services.AddSingleton<JellyfinClient>(sp =>
        {
            ArrkInstance arrk = sp.GetRequiredService<ArrkInstance>();
            IOptionsMonitor<JellyfinConfig> config = sp.GetRequiredService<IOptionsMonitor<JellyfinConfig>>();
            // Resolve the base URL (which follows services.jellyfin.proxy, the same flag that drives
            // Jellyfin's own BaseUrl) and the credentials per call so config edits apply live.
            return new JellyfinClient(
                () => $"http://{arrk.GetContainerName("jellyfin")}:{HttpPort}{(config.CurrentValue.Proxy ? "/jellyfin" : "")}",
                () =>
                {
                    JellyfinConfig current = config.CurrentValue;
                    return (current.AdminUsername, current.AdminPassword);
                },
                arrk.StackName);
        });

        services.AddSingleton<JellyfinService>();
        services.AddSingleton<JellyfinLinkService>();
        services.AddSingleton<ITvPlayerLinks>(sp => sp.GetRequiredService<JellyfinLinkService>());
        services.AddSingleton<JellyfinSsoInstaller>();

        // In-site web player (playback negotiation + stream proxy). Registered unconditionally so
        // the endpoints always resolve; behaviour is gated by services:jellyfin:web_player:enabled.
        services.AddSingleton<Arrk.Jellyfin.Playback.PlaybackSessionStore>();
        services.AddSingleton<Arrk.Jellyfin.Playback.JellyfinPlaybackService>();

        // Registered unconditionally: both are config-gated at call time (IsAvailable /
        // ManageUsers) so enabling or disabling Jellyfin (or user management) in the config editor
        // takes effect without an app restart. With Jellyfin absent they simply stay inactive.
        services.AddSingleton<IAccountProvisioner, JellyfinAccountProvisioner>();
        services.AddSingleton<IWatchStateProvider, JellyfinWatchStateProvider>();
        services.AddSingleton<JellyfinWatchActivityRecorder>();

        // The module owns the in-site player page and its script.
        services.AddSingleton<IHeadAssetProvider, JellyfinHeadAssets>();
    }

    public void ConfigureHostedServices(IServiceCollection services)
    {
        services.AddHostedService<JellyfinInitialiser>();
        services.AddHostedService<JellyfinImportScanTrigger>();
        services.AddHostedService<JellyfinWatchActivityMonitor>();
    }

    /// <summary>The module owns its in-site playback endpoints.</summary>
    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapJellyfinPlaybackEndpoints();

    public void ConfigureJobs(IArrkJobProvider jobs, IServiceProvider services)
    {
        jobs.RegisterScheduledAndOptionsChange<JellyfinConfigSyncJob, JellyfinConfig>(
            j => j.Execute(CancellationToken.None),
            JellyfinConfigSyncJob.DefaultCron);
        jobs.RegisterScheduled<JellyfinLibraryScanJob>(
            j => j.Execute(CancellationToken.None),
            JellyfinLibraryScanJob.DefaultCron);
    }
}
