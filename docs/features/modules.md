# Plugins and modules

Arrk's UI and backend are module-based. A plugin is a normal .NET class library whose DLL is
dropped into Arrk's `plugins/` folder. The host loads it at startup and wires it like a
built-in module: services, hosted services, jobs, HTTP endpoints, pages, navigation, region slots
and head assets. UI composition is in [`ui.md`](ui.md).

## The plugins folder

```
<data>/plugins/
  MyPlugin.dll                    your plugin and private dependencies
  MyPlugin/assets/plugin.css      served from /plugins/MyPlugin/assets/plugin.css
```

`<data>` is Arrk's data route (the working directory for a standalone run). The folder is
created automatically. Drop the DLLs in and restart Arrk.

The loader:

- loads each managed DLL into the default load context, recursively;
- skips a DLL whose simple name is already loaded, so a plugin cannot shadow `Arrk.Core`;
- resolves missing dependencies from beside the DLL;
- ignores native libraries;
- registers every `IArrkModule` it finds.

A runtime-loaded plugin cannot use `_content/<Assembly>/...` because that manifest is built into
the host. Serve assets from the plugins folder instead.

## A minimal plugin

Reference `Arrk.Core` for modules and services, `Arrk.Ui` for UI contracts, and the
feature abstraction assembly the plugin extends. Never reference a feature implementation such
as `Arrk.Tvarr` or `Arrk.Music`.

```csharp
public sealed class MyModule : IArrkModule
{
    public string ModuleName => "MyPlugin";
    public IReadOnlyList<Service> GetServices() => [];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<INavigationProvider, MyNavigation>();
        services.AddSingleton<IHeadAssetProvider, MyHeadAssets>();
    }
}
```

[`samples/Arrk.SamplePlugin`](../../samples/Arrk.SamplePlugin) is a complete,
compile-checked example with a module, navigation entry, Razor page and stylesheet.

## Module contract

Implement `IArrkModule` (`Arrk.Core.Modules`). Only `ModuleName` and `GetServices()` are
required; the rest are optional.

| Member | Purpose |
|---|---|
| `GetServices()` | Containers the module provisions (empty for a pure add-on) |
| `GetConfigSections()` | Sections shown in the config editor |
| `ConfigureServices` | DI and all UI contributions |
| `ConfigureHostedServices` | Background services |
| `ConfigureJobs` | Hangfire jobs |
| `ConfigureEndpoints` | The module's HTTP endpoints |
| `GetDependencies()` | Third-party licences shown in About |

## UI contributions

- `INavigationProvider`: menu entries with an `EnabledConfigPath` so they hide live.
- `IDiscoverSourceProvider`: a tab and component on Discover.
- `[UiRegion("region.id")]` or `IUiSlotProvider`: inject into named regions.
- `ITileProvider`: dashboard and page widgets. See [`tiles.md`](tiles.md).
- `IHeadAssetProvider`: styles and scripts in load order.
- `IExternalLoginProvider`: a sign-in method on the login page. The module owns the OAuth flow.
- `IExternalWatchlist`: a service-backed watchlist (for example MAL Plan to Watch) in the TV
  watchlist shelf without copying rows.
- `ITvPlayerLinks`: player deep links consumed by TV.
- `ISubtitleProvider`: the subtitle backend seam. See [`subtitles.md`](subtitles.md).

Normal `@page` components are picked up automatically.

### Head assets

A plugin's stylesheet is fetched from the plugins folder:

```csharp
public sealed class MyHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("/plugins/MyPlugin/assets/plugin.css", HeadAssetKind.Style, Order: 200)
    ];
}
```

### Theming

Build from the shared component library and use the `--st-*` tokens instead of literal colours.
The shared theme loads before the plugin's stylesheet, so overrides win.

## Discovery and debugging

`UiRegistry` discovers modules by scanning loaded assemblies, so a plugin needs no registration
file. It validates routes across the host and all modules and fails fast on a duplicate.

- The host logs `Loaded plugin assembly: <name>` and `Registered module: <name>` at startup.
- A contribution that throws while rendering is isolated by an `ErrorBoundary` and shows a small
  warning marker.
- A plugin that fails to load is logged and skipped.
