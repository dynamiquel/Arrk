# Watch activity, watchlists and media lifecycle

How Stackarr records what users watch and listen to, and how that drives watchlists, pinning and
stale-media cleanup.

## Purpose

Stackarr keeps one durable, per-user playback history across every player. It answers who watched
or listened to what, when, and on which service or device, and it is the clock behind stale-media
cleanup. Admins can pin anything to exclude it permanently.

## Model

The types live in Core so TV, music, the lifecycle module and plugins share one contract.

| Type | Role |
|---|---|
| `WatchActivityEntry` | One play/listen: user, service, item key, title, source, device, start, last seen, completion, progress, provider ids |
| `MediaConsumptionStamp` | One row per `(service, item key)` with the latest consumption; staleness reads this |
| `Watchlist` / `WatchlistItem` | Named per-user lists; adding never downloads anything |
| `MediaPin` / `MediaPinService` | Admin "never stale" markers |
| `IManagedMediaProvider` | Library-module contract exposing items, sizes and deletion |
| `StaleMediaService` | The lifecycle evaluator consumed through Core |
| `LifecycleItemState` | Last announced level per item, so warnings are not repeated |

### Identity

`MediaItemKeys` builds stable keys:

- Movie: `tv:movie:tmdb:{id}`
- Series: `tv:series:tvdb:{id}` (TMDb fallback)
- Season: `{seriesKey}:s{season}` — the TV staleness unit
- Episode: `{seriesKey}:s{season}e{episode}`, with the season as parent
- Album: `music:album:{artist}|{album}` (normalised)
- Track: `music:track:{artist}|{title}`, with the album as parent

Consuming a child resets its parent's clock. A pin on a parent protects every child. Library
playback supplies the album artist so a Various Artists album matches its album key.

## Sources

| Source | Service | Records |
|---|---|---|
| In-site music player | music | Track start, progress and parent album; device/client from the browser |
| In-site Jellyfin player | tv | Start, progress and completion; device/client from the browser |
| Jellyfin session poller | tv | Every active Jellyfin client with its device and client names |
| Jellyfin history backfill | tv | Already-played items on startup (device unknown) |

Navidrome keeps scrobbles per API account, but Stackarr uses one shared account, so plays made in
external Navidrome clients are not attributable to a Stackarr user. Plays through Stackarr's
player are tracked.

## Staleness rules

```yaml
monitoring:
  tv:
    stale:
      upperThreshold: 30       # days without consumption before stale
      lowerThreshold: 0        # early warning; 0 disables
      autoCleanup: false
      keepThreshold: 45        # longer threshold when bookmarked
      keepLowerThreshold: 0
  music:
    stale:
      upperThreshold: 30
      lowerThreshold: 0
      autoCleanup: false
      keepThreshold: 45        # album in any Navidrome playlist
      keepLowerThreshold: 0
```

Defaults are conservative: stale at 30 days, no early warning, no automatic deletion, 45 days for
bookmarked titles. Evaluation for one item:

1. A pinned item or parent is always Fresh.
2. `baseline = max(AddedAt, latest consumption for the item or parent)`.
3. `age = now - baseline`.
4. Bookmarked items (watchlist for TV, playlists for music) use the `keep*` thresholds.
5. `age >= upper` is Stale; `lower > 0 && age >= lower` is Expiring; otherwise Fresh.
6. `daysRemaining = upper - age` drives the countdown.

TV returns movies and downloaded seasons; music returns downloaded albums. Sizes come from the
provider and drive the space-saved detail.

## Notifications and cleanup

The hourly `StaleCleanupJob`:

- announces `media.lifecycle.about_to_be_stale` and `media.lifecycle.stale` once per level per
  item;
- resets the announced level when the item is consumed or removed;
- deletes stale items only when that service's `autoCleanup` is true;
- never deletes a pinned item;
- records `media.lifecycle.deleted` with bytes reclaimed.

Warnings are edge-triggered and survive restarts through `LifecycleItemState`.

## UI

- `/history`: per-user timeline; admins choose a user, filter by service/source and search.
- `/movies` and `/tv`: a tabbed Watchlists shelf with rename/delete and a New watchlist action.
  External lists from `IExternalWatchlist` appear as read-only shelves.
- `/stale` (admin): stale, about-to-be-stale and pinned tabs, per-item days remaining, one-off
  delete, delete-all, run-now and pin/unpin.
- Media dialogs: watchlist and (for admins) pin actions; cards carry a compact `StaleBadge`.

## Plugin contract

Plugins reference `Stackarr.Core` only:

- record playback with `WatchActivityService`;
- read history with `GetHistoryAsync` / `GetConsumptionAsync`;
- use `WatchlistService` and `MediaPinService`;
- implement `IExternalWatchlist` for a service-backed list such as MAL Plan to Watch;
- implement and register `IManagedMediaProvider` to make a library visible to staleness;
- read `StaleMediaService` for statuses.

No plugin needs `Stackarr.MediaLifecycle` or `Stackarr.WatchActivity`.

## Invariants

- A play resets the clock for its item and its parent.
- TV staleness is per season; movies and music albums are single items.
- Watchlist names are capped at 30 characters and each user can own at most 50 lists.
- Bookmarked titles get a longer grace period, not immortality.
- Pinned items are never stale, notified or auto-deleted.
- Defaults never auto-delete.
- The lifecycle module never references a library module, or vice versa.
