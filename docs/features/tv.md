# TV: movies and shows

How the `tv` concept is implemented. Music has its own guide in [`music.md`](music.md).

## Model

Radarr is the library of record for movies, Sonarr for TV and anime, TMDB is discovery and
metadata, and Stackarr is the single UI driving both.

Stackarr keeps no TV library database. Every "do I have this?" answer comes from Radarr or
Sonarr; TMDB is used for search, artwork and metadata. The arrs remain the source of truth and
their UIs stay usable for advanced work.

```
Stackarr UI (/movies, /tv, /discover/*)
       |                    |
  library + writes     discovery/metadata
       v                    v
  Radarr / Sonarr         TMDB
       |     |
   Prowlarr  Transmission
       |
  /media/tv/{movies,shows}
       |
   Jellyfin
```

`Stackarr.Tvarr` registers the `tv` service, its DI graph, jobs and Razor class library.
Domain/provider keys (`TvMediaDomains`, `TvProviderIds`, `TvReleaseKinds`) live in
`Stackarr.Tv.Abstractions`.

## Containers and config

Both containers use the LinuxServer images, the stack network, named config volumes, a
`/downloads` read-only bind and `$tv:movies` → `/movies` (Radarr) or `$tv:shows` → `/shows`
(Sonarr) read-write. API keys default to `stackarr`. They provision after `indexer`,
`torrent_downloader` and `bazarr` (when the optional backend is enabled), so Prowlarr,
Transmission and Bazarr exist first. Proxy is opt-in per service and exposes the arr's own
UI/API.

| Key | Default | Effect |
|---|---|---|
| `services.tv.enabled` | `true` | Hides the TV UI, Discover tabs and request handler; jobs keep running |
| `services.tv.proxy` | `false` | Publishes `/radarr` and `/sonarr` through Traefik |
| `services.tv.failedDownloads.retriggerSearch` | `true` | Blocklists failed releases and searches again |
| `services.tv.missingSearch.enabled` | `true` | Hourly sweep for monitored items still missing a file |
| `services.tv.search.enabled` | `true` | Shows search, retry and interactive-search actions; disable for a local-only library |

Quality profiles come from the top-level `qualityProfiles` section, including
`minimumBitratesMbps` per resolution tier (defaults `1080p: 3`, `2160p: 12`; `0` leaves a tier
unmanaged). TMDB credentials live under `metadata` (with root-level fallbacks). `storage.tv`
defaults to `$media/tv`.

## Sync jobs

| Job | Cadence | Purpose |
|---|---|---|
| `RootFolderSyncJob` | 30s + config change | Adds `/movies` and `/shows` root folders |
| `QualityProfileSyncJob` | 30s + config change | Applies bitrate floors, custom formats and configured profiles |
| `DownloadClientSyncJob` | 30s + config change | Creates/repairs the Transmission client |
| `ProwlarrAppSyncJob` | 30s + config change | Registers Radarr and Sonarr in Prowlarr |
| `FailedDownloadHandlingSyncJob` | 30s + config change | Keeps `autoRedownloadFailed` in sync |
| `MediaImportMonitorJob` | 1 min | Emits import notifications |
| `MissingContentSearchJob` | Hourly | Searches monitored missing movies/episodes |
| `TorrentCleanupJob` | 1 min | Processes failed-torrent cleanup rows |

Quality profile sync deletes every arr profile not listed in `qualityProfiles.profiles` and never
updates an existing profile, so the configured list is authoritative.

## Library model

`MediaItem` is the single view model for library and discovery. It carries identity, artwork,
quality/technical info, library state, download state, watch state and request state.

| State | Meaning |
|---|---|
| `Downloaded` | Movie has a file; series has every aired episode |
| `Downloaded Monitored` | Every episode in monitored seasons is present |
| `Partial` | Some episodes or files exist |
| `Missing` | Nothing is present |
| `Not Released` | No file and the release window is closed |
| Downloading | A matching arr queue item exists |

Specials (season 0) are ignored for season counts and progress. `ReleaseStatus` is the single
decision point for the Not Released badge.

## Discovery and search

`MediaCatalogService` answers from TMDB when configured and falls back to arr lookup. Library
membership is always overlaid from Radarr/Sonarr by TMDB or TVDB id.

- Movies: Trending, Popular, Top Rated, Upcoming, Now Playing, New Releases.
- Shows: Trending, Popular, Top Rated, On The Air, Airing Today, New Releases.
- Anime: animation genre with an East Asian origin/language filter, excluding kids, with
  seasonal browsing.
- Search uses TMDB multi/movie/TV search, or arr lookup without TMDB.
- Discover uses TMDB discover with genre, year and sort filters.

Without TMDB credentials, Discover warns and only local/arr-backed search works.

## Adding and searching

- Movie: adds under `/movies/<tmdbId>` with `MinimumAvailability = "released"`, the chosen
  profile and optional immediate search.
- Series: adds under `/shows/<tvdbId>` with seasons/episodes monitored per the dialog.

Search is delegated to the arrs through native commands (`SeriesSearch`, `SeasonSearch`,
`EpisodeSearch`, `MoviesSearch`). Interactive search uses `/api/v3/release` and the shared
release picker. Stackarr does not implement release selection, loop avoidance or queue awareness.

Interactive candidates are mapped to a shared `ReleaseCandidate` (title, indexer, protocol, size,
seeders, score, rejection reasons, grab keys). Rejected releases are hidden by default. A
series-level interactive search is not offered because multiple seasons make one release list
ambiguous.

## Local import

Administrators can add files they already own without an indexer. Uploads are staged under
`$downloads/stackarr-uploads` and handed to Radarr/Sonarr with `ManualImport` in copy mode, so the
arr still chooses the final path, quality and metadata. Discovery offers **Upload** beside
**Add to Library**; movie, season and episode dialogs offer upload; the library pages offer
auto-detect upload with a scored match confirmation. Local import is admin-only and accepts only
the supported video containers plus optional sidecar subtitles. See
[`local-import.md`](local-import.md).

## Download and import lifecycle

1. The arr grabs through Transmission.
2. The arr imports into `/movies` or `/shows`.
3. `MediaImportMonitor` polls history for `downloadFolderImported` records and records a
   `downloads.imported` notification with artwork. A season pack is announced once as a range.
4. Library pages poll the arrs (10s movies, 15s shows) for status and watch state.

On first run the monitor looks back two hours only, so an existing library is not replayed.

## Deletion and failed downloads

Deleting a movie or series removes matching torrents from Transmission (arr deletes do not),
deletes the arr entry, then publishes `MediaRemovedEvent` so Requests can cancel outstanding
requests.

Failed downloads have two paths, both governed by `services.tv.failedDownloads.retriggerSearch`:

1. The arr's own handling, kept enabled by `FailedDownloadHandlingSyncJob`.
2. Stackarr-owned cleanup: when DownloadManagement deletes a slow, stalled or errored torrent, TV
   maps its category (`tv` → Sonarr, `movies` → Radarr, otherwise both), writes a
   `PendingTorrentCleanup` row and resolves it through the arr's `ResolveFailedDownload`, which
   blocklists the release and searches again. It gives up after five attempts. Error deletions
   are ignored when the switch is off; slow/stalled/seeding cleanup always queues.

`MissingContentSearchJob` retries unavailable releases hourly, independent of failure events.

## Requests

`TvarrRequestHandler` contributes movie, series, season and episode capabilities. Two TV-specific
rules matter: an existing series makes season/episode requests look already-in-library, and
quality profile ids are merged across Radarr and Sonarr even though the backends have independent
namespaces. See [`requests.md`](requests.md).

## Rules and sharp edges

1. `enabled: false` hides the UI only; sync and cleanup jobs keep calling the arrs.
2. Configured quality profiles are authoritative; `CutoffQuality` is ignored.
3. The arrs ship `minSize = 0`, so size alone never rejects a release. The sync job applies the
   configured minimum bitrates (converted to MB/min) to every matching quality definition.
   Existing files are kept, but the arr will not grab below the floor again.
4. `qualityProfileName`, `qualities` and `indexerSync` in `services.tv` are inert.
5. `/downloads` is read-only in both arrs.
6. Root folders must exist first; `GetDefaultRootFolder` falls back to `/tv` even though the mount
   is `/shows`, and the sync job creates the real one within 30 seconds.
7. Upgrade downloads are not shown as "downloading" on library pages.
8. The shared release picker assumes one provider and routes a grab by indexer name.
9. Without TMDB, anime fallback filters only the Animation genre and does not set the anime flag.
10. Search is delegated to the arrs; Stackarr does not fan out searches itself.
