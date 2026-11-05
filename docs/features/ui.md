# UI architecture

How Stackarr's UI is composed from modules. For plugins see [`modules.md`](modules.md).

The shell (`Stackarr.App`) never references a module's UI. It renders whatever the registered
`Stackarr.Ui` contracts describe, and every module, built-in or drop-in, contributes through
those contracts.

## Layers

```
Stackarr.App        host: Blazor app, layout, router, module and plugin loader
  +- Stackarr.Ui    shared UI layer every module builds against
       +- contracts  navigation, discover, regions, head assets, tiles, activity
       +- components primitives, theme and component JS (RCL)
Stackarr.<Module>   Razor class library owning its pages, dialogs, styles, scripts and endpoints
```

`Stackarr.Ui.Components` holds the reusable primitives (`PageHeader`, `StTabs`,
`StToggleChips`, `SearchBox`, `StChip`, `StatTile`, `StCardSheetDialog`, `UiRegion`, ...), the
shared theme (`wwwroot/css/{app,site}.css`) and the component JS. `UiRegistry` publishes them as
built-in head assets.

Typography is one rem ladder; `StackarrTheme` mirrors it for MudBlazor. Body text and tabs are
`--st-font-size-md` (1rem), secondary metadata is `--st-font-size-sm`, and `--st-font-size-2xs`
(0.75rem) is the floor for visible text. Only the root declares a pixel size.

Accounts choose Small / Medium / Large under Account → Appearance. The choice is stored per user
and mirrored to a cookie that the head script applies before first paint, so text size does not
flash.

## Contributions

| Contract | What it adds | Rendered by |
|---|---|---|
| `INavigationProvider` | Menu entries (section, order, admin-only, `EnabledConfigPath`) | `NavMenu` |
| `IDiscoverSourceProvider` | A tab and hosted component on Discover | `Discover` |
| `IUiSlotProvider` or `[UiRegion]` | A component injected into a named region | Wherever the region renders |
| `IHeadAssetProvider` | Styles and scripts, in load order | `<head>` and end of `<body>` |
| `ITileProvider` | Dashboard/page widgets | Dashboard and pages |
| `IStackarrModule.ConfigureEndpoints` | The module's HTTP endpoints | Mapped by the host |
| `IWatchStateProvider` | Player history and continue-watching | Watch-state facade |

`UiRegistry` composes everything once at startup, filters each contribution by its
`EnabledConfigPath`, raises `Changed` on config reload, and rejects duplicate Blazor routes
across the host and its modules.

Navigation entries with a `LogoUrl` render a brand image instead of an icon font, but keep the
same rail/grid shape as every other `MudNavLink`. `app.css` lists both variants in shared rules
and `CssNavigationTests` pins that pairing.

## Regions

A region is a named insertion point rendered with `<UiRegion Name="..." />`. Contributions are
isolated by an `ErrorBoundary`, so a broken contribution cannot take down the page.

| Region | Rendered by | Typical contributions |
|---|---|---|
| `shell.appbar.actions` | `MainLayout` | Account chip, notification bell, assistant launcher |
| `shell.overlays` | `MainLayout` | Music player overlay |
| `player.overlays` | `Watch` | Media detail dialog |
| `discover.actions` | `Discover` | "My Movies", "My Shows", "My Music" |
| `dashboard.alerts` | `Dashboard` | Pending accounts |
| `account.connections` | `/account` | Discord, MyAnimeList |
| `tv.series.season` | `SeriesDetailDialog` | MAL status and score |

A module adds a component with `[UiRegion("<region>", Order = ...)]`, or an `IUiSlotProvider`
when it needs parameters, `Prepend`/`Replace` modes, or a wildcard region such as
`tv.*.actions`.

## Tiles and availability

Dashboard widgets are tiles, not raw slots. A module registers an `ITileProvider`; the account
arranges tiles into Tile Sections in a per-account YAML document. Tiles may link to their owning
page, can be admin-only or link-only-for-admins, and are filtered live by their service's
`enabled` flag. See [`tiles.md`](tiles.md).

Every contribution may carry an `EnabledConfigPath` (for example `services:tv`). When that
section's `enabled` flag is false the contribution is hidden live, without the shell knowing
which module owns it. DI, hosted services and jobs stay registered; the flag gates the UI.

## Assets and routing

- The shared theme loads first (`app.css`, then `site.css`); module stylesheets load after, so a
  module override wins.
- Component JS loads first; module scripts load after.
- A drop-in plugin cannot use `_content`; its assets live under `<data>/plugins` and are served
  from `/plugins/...`.
- Modules ship normal `@page` components. `IUiAssemblyProvider` tells both the interactive and
  prerender routers which assemblies hold pages. `UiRegistry` fails fast on a duplicate route.

## Building a module's UI

1. Make the module a Razor class library and reference `Stackarr.Ui`.
2. Register `INavigationProvider`, `IDiscoverSourceProvider`, `IUiSlotProvider`,
   `IHeadAssetProvider` or `ITileProvider`; set `EnabledConfigPath` where relevant.
3. Add `@page` components built from the shared primitives.
4. Map endpoints and jobs from the module.
5. Put CSS/JS under the module's `wwwroot` and register them through `IHeadAssetProvider`.
