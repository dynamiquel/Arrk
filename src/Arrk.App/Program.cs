using Hangfire;
using Hangfire.Storage.SQLite;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Serilog;
using Arrk.App;
using Arrk.App.Services;
using Arrk.App.Services.Ui;
using Arrk.Ui;
using Arrk.Configuration;
using Arrk.Core;
using Arrk.Auth.Abstractions;
using Arrk.Core.Configuration;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Modules;
using Arrk.Core.Playback;
using Arrk.Core.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.FileProviders;
using Arrk.Docker;
using Arrk.Navidrome;
using Arrk.Orchestration;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<LogCaptureService>();

// Arrk runs behind Traefik and (optionally) a Cloudflare tunnel. Honour the forwarded
// scheme/host so generated URLs (OIDC discovery and callback endpoints, deep links) use the
// public HTTPS origin rather than the internal HTTP one.
builder.Services.Configure<ForwardedHeadersOptions>(ForwardedHeadersConfiguration.Configure);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

builder.Host.UseSerilog();

// The data route anchors Arrk's own files (config, database) and default storage roots.
// It defaults to the current working directory for standalone runs and is set to "/" in the
// shipped container. Users can override it with the ARRK_DATA_ROUTE environment variable.
string dataRoot = DataRoute.Root;
builder.Configuration
    .AddYamlFile(ArrkFiles.ConfigPath(dataRoot), optional: true, reloadOnChange: true)
    .AddJsonFile(ArrkFiles.ConfigJsonPath(dataRoot), optional: true, reloadOnChange: true);

builder.Services.AddOptions<ArrkConfig>()
    .Bind(builder.Configuration)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<QualityProfilesConfig>()
    .ConfigureConfig(builder.Configuration.GetSection("qualityProfiles"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.Configure<RoutesConfig>(builder.Configuration.GetSection("routes"));

builder.Services.AddArrkConfiguration();
builder.Services.AddArrkCore();

builder.Services.AddSingleton<UiTextSizePreference>();
builder.Services.AddArrkTiles();

// Bind the metadata section so metadata.tmdb_api_key / tmdb_read_token hot-reload; the TMDB
// client resolves them live and drops its response cache when they change.
builder.Services.Configure<MetadataConfig>(builder.Configuration.GetSection("metadata"));

string configDir = ArrkFiles.ConfigDirectory(dataRoot);
builder.Services.AddSingleton(sp =>
    new ArrkInstance(builder.Configuration, configDir));

// Navidrome API credentials: prefer config, otherwise generate + persist once so the
// auto-provisioner and the Subsonic client agree across restarts.
builder.Services.AddSingleton(NavidromeApiSecret.Load(
    configDir,
    builder.Configuration["services:navidrome:apiUsername"],
    builder.Configuration["services:navidrome:apiPassword"]));

// Signs the self-authenticating stream URLs that let AirPlay receivers (Apple TV / HomePod) fetch
// playback without the browser's cookies. Persisted beside the config so tokens survive restarts.
builder.Services.AddSingleton(StreamTokenService.Load(configDir));

// The UI registry composes every module's navigation, regions and head assets; the surface gate
// resolves their live enabled state so toggling a module on/off is reflected without a restart.
// Both are resolved lazily, after every module has registered its contributions.
builder.Services.AddSingleton<IUiAssemblyProvider, UiAssemblyProvider>();
builder.Services.AddSingleton<INavigationProvider, AppNavigationProvider>();
builder.Services.AddSingleton<IHeadAssetProvider, AppHeadAssetProvider>();
builder.Services.AddSingleton<UiRegistry>();
builder.Services.AddSingleton<MediaDetailLauncher>();
builder.Services.AddSingleton<SubtitleSearchLauncher>();

// Music discovery, library and acquisition are contributed by the Arrk.Music module; the
// activity center (shared by the shell and modules) lives in Arrk.Ui.
builder.Services.AddSingleton<ActivityService>();
builder.Services.AddHostedService<LiveActivityBridge>();
builder.Services.AddHostedService<SoulseekAvailabilityActivity>();

string dbPath = ArrkFiles.DatabasePath(dataRoot);
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddArrkDocker();

builder.Services.AddHangfire(hangfireConfig => hangfireConfig
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSQLiteStorage(HangfireStorage.CreateConnectionFactory(HangfireStorage.DatabasePath(dataRoot))));
// The default server drains the general queue; music acquisition can enqueue hundreds of slow
// (multi-minute) searches, which would otherwise starve the every-minute Music Download Import and
// Soulseek failure check. A small dedicated server owns the "music" queue so those always run.
builder.Services.AddHangfireServer();
builder.Services.AddHangfireServer(options =>
{
    options.Queues = [ArrkHangfireJobProvider.MusicQueue];
    options.WorkerCount = 2;
});
// Long music acquisition searches get their own workers so a backlog can't starve the default
// queue (download-management, config, etc.) or the music import queue.
builder.Services.AddHangfireServer(options =>
{
    options.Queues = [ArrkHangfireJobProvider.AcquisitionQueue];
    options.WorkerCount = 3;
});
builder.Services.AddSingleton<JobLoggingFilter>();

builder.Services.AddSingleton<IArrkJobProvider, ArrkHangfireJobProvider>();
builder.Services.AddSingleton<IEventBus, EventBus>();

ConfigSectionRegistry configSectionRegistry = new();
ConfigDefaults.RegisterCoreSections(configSectionRegistry);
builder.Services.AddSingleton(configSectionRegistry);

ModuleLoader moduleLoader = ServiceRegistryFactory.CreateModuleLoader();
builder.Services.AddSingleton(moduleLoader);

// Drop-in plugins: load every managed DLL under <data>/plugins before the container is built, so a
// plugin registers its services/jobs/endpoints/UI exactly like a built-in module. Its static assets
// are served from the same folder (a runtime-loaded assembly has no _content manifest).
string pluginsDirectory = ArrkFiles.PluginsDirectory(dataRoot);
Directory.CreateDirectory(pluginsDirectory);
moduleLoader.LoadPlugins(pluginsDirectory);

moduleLoader.ConfigureAllServices(builder.Services, builder.Configuration);
ServiceRegistry registry = moduleLoader.BuildRegistry();
builder.Services.AddSingleton(registry);
moduleLoader.RegisterConfigSections(configSectionRegistry);

builder.Services.AddSingleton<ConfigSchemaGenerator>(sp =>
    new(configSectionRegistry, sp));
builder.Services.AddSingleton<ConfigValidator>(sp =>
    new(sp.GetRequiredService<ConfigSchemaGenerator>(), configSectionRegistry, sp));
builder.Services.AddSingleton<ConfigFormBuilder>(sp =>
    new(sp.GetRequiredService<ConfigSchemaGenerator>(), configSectionRegistry));
builder.Services.AddArrkOrchestration(registry);
moduleLoader.ConfigureAllHostedServices(builder.Services);

builder.Services.AddDbContextFactory<ArrkDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

WebApplication app = builder.Build();

var logCapture = app.Services.GetRequiredService<LogCaptureService>();
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.Sink(logCapture)
    .CreateLogger();

Log.Information("Arrk v{Version} starting", ArrkVersion.Current.ToString());

// Log every Hangfire job run globally (start / finish / failure).
GlobalJobFilters.Filters.Add(app.Services.GetRequiredService<JobLoggingFilter>());

moduleLoader.ConfigureAllJobs(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseForwardedHeaders();

// Traefik/cloudflared can deliver the request to Arrk over plain HTTP even though the public
// origin is HTTPS, which makes OpenIddict advertise http:// endpoints (rejected by OIDC clients).
// When forwarded headers are missing/untrusted, force HTTPS for requests whose host matches the
// configured public URL so generated URLs use the public scheme. The public URL is read per
// request so `routes.public_url` can be changed live.
app.Use(async (context, next) =>
{
    RoutesConfig routes = context.RequestServices.GetRequiredService<IOptionsMonitor<RoutesConfig>>().CurrentValue;
    string? forwardedHost = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault();
    if (PublicSchemeResolver.ShouldForceHttps(context.Request.IsHttps, forwardedHost, context.Request.Host.Value, routes))
        context.Request.Scheme = "https";

    await next();
});

app.UseArrkSecurityHeaders();
app.UseStaticFiles();

// Plugin static assets. A runtime-loaded plugin cannot use `_content/<Assembly>/...` (its static
// web-asset manifest is not part of the host), so its CSS/JS/images live under <data>/plugins and are
// served from `/plugins/...` (the plugin points its head assets there).
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(pluginsDirectory),
    RequestPath = "/plugins"
});

// Anonymous credential endpoints are cheap to spam on the public internet. The limiter runs
// after routing (so it sees the endpoint policy) but before authentication, so rejected attempts
// cannot create work for Identity or OpenIddict.
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Each module maps its own HTTP endpoints (playback proxies, art endpoints, ...) without the host
// knowing about it.
moduleLoader.ConfigureAllEndpoints(app);

// Disclose module assemblies to the endpoint router (so module @page components resolve on the
// initial static SSR pass as well as interactively) and validate routes across every module before
// serving. Resolving the registry here fails fast on a duplicate route.
IUiAssemblyProvider uiAssemblies = app.Services.GetRequiredService<IUiAssemblyProvider>();
_ = app.Services.GetRequiredService<UiRegistry>();
app.MapRazorComponents<Arrk.App.Components.App>()
    .AddAdditionalAssemblies(uiAssemblies.AdditionalAssemblies.ToArray())
    .AddInteractiveServerRenderMode();

app.MapHangfireDashboard("/hangfire", new()
{
    DashboardTitle = "Arrk Scheduler",
    Authorization = [new HangfireAdminAuthorizationFilter()]
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = ArrkVersion.Current.ToString(), timestamp = DateTime.UtcNow }));

app.MapArrkConfigEndpoints();

app.Run();
