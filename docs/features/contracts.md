# Contracts and abstractions

How Stackarr keeps feature implementations replaceable. UI composition is in
[`ui.md`](ui.md); architecture status is in [`../plan.md`](../plan.md).

## Layers

```
Stackarr.Core                      kernel: modules, services, config, events, shared DB
  +- Stackarr.Media.Abstractions   media primitives and capability interfaces
  +- Stackarr.Tv.Abstractions      TV models and library/metadata/player contracts
  +- Stackarr.Music.Abstractions   music models and library contracts
  +- Stackarr.Downloads.Abstractions torrent and release contracts
  +- Stackarr.Auth.Abstractions    accounts, roles, claims and provider contracts
  +- Stackarr.Subtitles.Abstractions subtitle provider, targets and candidates
```

Abstraction assemblies never reference a feature implementation. A plugin references only the
contract assemblies it uses plus `Stackarr.Ui` for UI.

## Capabilities

Interfaces expose their members directly; a model implements only the capabilities it has.

| Interface | Means |
|---|---|
| `IMediaDescriptor` | Title, year, release date, overview, genres, rating, runtime, certification |
| `IMediaArtwork` | Poster, fanart, banner and logo URLs |
| `IMediaLibraryState` | Monitored, file/size, added date, quality and technical file |
| `IMediaDownloadState` | Live download progress and status |
| `IMediaWatchState` | Player progress, watched flag and resume position |
| `IMediaRequestState` | Request lifecycle state |

A metadata result can implement descriptor and artwork only; a library row adds library state; a
UI card adds download, watch and request state. Consumers pattern-match (`item is IMediaArtwork`)
and degrade when a capability is absent.

Identity interfaces live with the feature that owns the provider: `ITmdbIdentity`,
`ITvdbIdentity` and `IImdbIdentity` in `Stackarr.Tv.Abstractions`, and `IDeezerIdentity` in
`Stackarr.Music.Abstractions`. Provider-id keys follow the same split (`TvProviderIds`,
`MusicProviderIds`).

## Media domains

`MediaDomain` is an open string value, not a central enum. Each feature owns its keys
(`TvMediaDomains.Movie`, `MusicMediaDomains.Music`), and a plugin can add a domain without
changing Core. Watch activity, watchlists and requests persist the domain as text through an EF
value converter.

## Feature seams

| Feature | Contract assembly | Implementation |
|---|---|---|
| TV | `Stackarr.Tv.Abstractions` | `Stackarr.Tvarr` |
| Music | `Stackarr.Music.Abstractions` | `Stackarr.Music` |
| Downloads | `Stackarr.Downloads.Abstractions` | `Stackarr.Transmission`, `Stackarr.DownloadManagement` |
| Auth | `Stackarr.Auth.Abstractions` | `Stackarr.Auth` |
| Subtitles | `Stackarr.Subtitles.Abstractions` | `Stackarr.Subtitles` dispatcher; `Stackarr.Subtitles.OpenSubtitles`, `Stackarr.Bazarr` providers |

Key seams: `IMovieLibrary`, `ISeriesLibrary`, `ITvCatalog`, `ITvPlayerLinks`, `IMusicLibrary`,
`ITorrentDownloader` (split into reader/commands/stats roles) and `ISubtitleProvider`.

`ReleaseSearchQuery` is domain-neutral: it carries a `MediaDomain`, a feature-owned `Kind`, a
provider-id dictionary, a generic library id and the season/episode or artist/album context a
source understands. There is no central `ReleaseDomain` enum.

## Rules

1. Abstractions contain interfaces, DTOs, enums and pure helpers only. No DI, HTTP, EF or Razor.
2. Modules and plugins reference another feature only through its abstraction assembly. A
   built-in integration that needs another module's UI is an implementation-level exception.
3. Contracts are consumer-driven; add a role only when a real consumer needs it.
4. Implementations may expose a composite interface, but consumers depend on the narrow role they
   use.
5. Provider-specific mapping stays in the implementation.
6. Resolution labels resolve through one alias table (`2160p`/`4K`/`UHD`, `1080p`/`FHD`,
   `720p`/`HD`, `480p`/`SD`).
7. Plugin source is not kept compatible across the 0.x line; the sample plugin and docs are the
   reference.
