using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Ai;
using Stackarr.Core.Notifications;
using Stackarr.Core.Services;
using Stackarr.DownloadManagement;
using Stackarr.Notifications;
using Stackarr.Orchestration;
using Stackarr.Speedtest;

namespace Stackarr.App;

public static class ServiceRegistryFactory
{
    /// <summary>
    /// Creates a ServiceRegistry from all built-in modules.
    /// Uses explicit module registration for compile-time safety.
    /// </summary>
    public static ServiceRegistry CreateDefault()
    {
        return CreateModuleLoader().BuildRegistry();
    }

    /// <summary>
    /// Creates a ModuleLoader pre-loaded with all built-in modules.
    /// Useful for extending with additional modules at runtime.
    /// </summary>
    public static ModuleLoader CreateModuleLoader(ILogger<ModuleLoader>? logger = null)
    {
        if (logger is null)
        {
            ILoggerFactory loggerFactory = new NullLoggerFactory();
            logger = loggerFactory.CreateLogger<ModuleLoader>();
        }

        ModuleLoader loader = new(logger);
        RegisterBuiltinModules(loader);
        return loader;
    }

    static void RegisterBuiltinModules(ModuleLoader loader)
    {
        loader.RegisterModule(new Auth.AuthModule());
        loader.RegisterModule(new OrchestrationModule());
        loader.RegisterModule(new Configuration.ConfigurationModule());
        loader.RegisterModule(new DownloadManagementModule());
        loader.RegisterModule(new NotificationModule());
        loader.RegisterModule(new AiModule());
        loader.RegisterModule(new NotificationDeliveryModule());
        loader.RegisterModule(new TraefikModule());
        loader.RegisterModule(new GluetunModule());
        loader.RegisterModule(new Cloudflared.CloudflaredModule());
        loader.RegisterModule(new Tvarr.TvModule());
        loader.RegisterModule(new Music.MusicModule());
        loader.RegisterModule(new Slskd.SlskdModule());
        loader.RegisterModule(new Jellyfin.JellyfinModule());
        loader.RegisterModule(new Navidrome.NavidromeModule());
        loader.RegisterModule(new Prowlarr.ProwlarrModule());
        loader.RegisterModule(new Transmission.TransmissionModule());
        loader.RegisterModule(new Stackarr.Subtitles.SubtitlesModule());
        loader.RegisterModule(new Stackarr.Subtitles.OpenSubtitles.OpenSubtitlesModule());
        loader.RegisterModule(new Bazarr.BazarrModule());
        loader.RegisterModule(new SpeedtestModule());
        loader.RegisterModule(new Requests.RequestsModule());
        loader.RegisterModule(new WatchActivity.WatchActivityModule());
        loader.RegisterModule(new MediaLifecycle.MediaLifecycleModule());
        loader.RegisterModule(new Mal.MalModule());
    }
}
