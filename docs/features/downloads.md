# Downloads

How Arrk provisions download clients, watches what they do, and enforces the download
lifecycle.

## Model

Transmission downloads torrents, slskd downloads from Soulseek, and
`Arrk.DownloadManagement` owns the rules, events and unified `/downloads` page.

- Transmission is core infrastructure for TV, movie and music torrents. It implements
  `ITorrentDownloader`.
- slskd is used by music for track and album grabs. It has its own REST client and transfer list.
- DownloadManagement has no container and no `Service`. It applies rules to whatever the torrent
  client reports. Soulseek transfers appear on the page but are not part of the torrent event or
  health pipeline.

## Transmission

`TransmissionServiceDefinition` declares `torrent_downloader` as infrastructure. It is always
enabled; `services.torrentDownloader.enabled` is not consulted.

- LinuxServer image, stack network, named config volume, `$downloads` read-write bind.
- RPC on 9091 with basic auth (`transmission`/`arrk`); peer port 51413 is internal only.
- Proxy is opt-in (`/transmission`).
- `NetworkMode = container:<stack>-gluetun` when VPN-routed; no peer port is published.

`TransmissionClient` speaks JSON-RPC, handles the 409/session-id handshake, uses a 15-second
timeout and resolves its base URL per request so VPN toggles apply live. `TransmissionService`
maps state, filters client-side, maps the first label to a category and treats any non-zero
Transmission error as `TorrentState.Error`.

`TransmissionLimitsJob` runs every minute and on config change while the service is healthy,
applying speed limits and maximum active downloads/seeds. `MaxActiveTorrents` is configured and
displayed but not sent to Transmission.

## Soulseek (slskd)

`SlskdServiceDefinition` governs `services.slskd`, enabled by default. The container mounts a
config volume at `/app` and `$downloads` read-write, with download roots at
`/downloads/slskd/{,incomplete,shares}`. The three subdirectories are created because slskd
refuses to start without them.

- Speed caps come from `services.torrentDownloader.speed` and are passed as environment.
- `SlskdClient` logs in for a JWT, caches it and clears it on `401`. It exposes search,
  enqueue, transfers, cancel/remove and option patching.
- `SlskdService` gates concurrent searches, polls until complete or 40 responses, removes failed
  transfers older than retention and applies configured limits.
- `SlskdConnectionWatchdog` restarts the container after two minutes offline, with a two-minute
  cooldown. A disconnected slskd does not mark music requests failed; the acquisition queue
  simply skips while unavailable.

No seeding lifecycle applies to Soulseek. Completed transfers are removed after import when
`removeCompletedAfterImport` is true.

## Download management

`DownloadManagementModule` contributes no container. It registers the event monitor, health
detector, error/seeding/storage cleanup, disk-space probe, storage tile, navigation and five jobs.
Rules live under `services.torrentDownloader.management`.

### Event pipeline

`DownloadEventMonitor` hydrates from the newest 5,000 download events, fetches all torrents and
diffs them:

- new hash → `torrent.added`;
- completion → `torrent.completed`;
- each 10% progress boundary → `torrent.progress` (audit only);
- error, pause, resume and first stall transitions → their events;
- a disappeared hash → `torrent.removed`;
- any other state change → `torrent.state` (audit only).

Soulseek transfers are not in this pipeline.

### Health and cleanup

`TorrentHealthCheckJob` only considers actively downloading, incomplete torrents. Stalled means
zero speed with no peers/seeds; slow means at or below `slow.thresholdMbps`. When the measured
connection is unhealthy, every penalty clock is held and nothing is deleted, so a bad line cannot
cause removals. Penalties accrue per minute (weighted by `zeroProgressMultiplier` at ≤5% progress)
and decay when healthy. At the limit the torrent is deleted with files and
`TorrentDeletedEvent(Stalled|Slow)` is published.

`SeedingLimitCleanupJob` keeps completed torrents until `seeding.minTimeMinutes`, then deletes
when `maxRatio` or `maxTimeMinutes` is reached. Seeding goals are enforced by deleting, not by
Transmission's ratio/idle limits.

`StorageCheckJob` probes the `/downloads` filesystem. At or above
`cleanupSeedingAtPercent` (85%) it deletes seeding torrents by the configured strategy until usage
drops; at or above `pauseDownloadsAtPercent` (90%) it pauses everything and emits critical events,
skipping cleanup. There is no auto-resume, and storage deletes do not publish
`TorrentDeletedEvent`, so TV/music are not asked to re-search.

`ErrorTorrentCleanupJob` records the first error sighting and deletes the torrent with files after
`errors.deleteAfterMinutes`. Recovery clears the timer. It does not check progress, so a completed
torrent with a tracker error can also be selected.

| Job | Cadence | Role |
|---|---|---|
| `DownloadEventMonitorJob` | 1 min | Event log and notifications |
| `TorrentHealthCheckJob` | 1 min | Slow/stalled penalties and deletion |
| `ErrorTorrentCleanupJob` | 1 min | Error-window deletion |
| `SeedingLimitCleanupJob` | 5 min | Ratio/time seeding goals |
| `StorageCheckJob` | 1 min | Storage pause and cleanup |

## The Downloads page

`/downloads` is always visible and merges torrents and Soulseek transfers. It refreshes every
three seconds, groups by Downloading, Queued, Seeding, Downloaded, Paused and Error, and shows
progress, size, ETA, speeds, ratio and peers per card. Admins can pause, resume, cancel, pause/
resume all and revoke a penalty. `?select=<hash>` highlights a card.

Manual cancellation does not publish `TorrentDeletedEvent`, so it is not treated as a failed
download.

## Interactions with TV and music

| Producer | Consumer | Effect |
|---|---|---|
| Slow/stalled/error/seeding delete | TV `DownloadEventHandler` | Writes `PendingTorrentCleanup`; the arr blocklists and searches again |
| Slow/stalled/error delete | Music `MusicFailureResolver` | Blacklists the source and re-queues the album |
| Soulseek failure | `SoulseekFailureCheckJob` | Blacklists the source, cancels and re-queues |
| Arr import | TV `MediaImportMonitor` | `downloads.imported` notification |
| Music import | `MusicDownloadImportJob` | Library placement and `media.music.*` events |

TV routing uses the first Transmission label: `tv` → Sonarr, `movies` → Radarr, anything else →
both. Music torrents use the `music` label.

## VPN routing

- Opt-in per service: `services.torrentDownloader.vpn` / `services.slskd.vpn`.
- Either flag provisions Gluetun even when `vpn.enabled` is false.
- Routed containers use Gluetun's namespace and are reached through its container name; clients
  resolve this per call, so toggling is live.
- No peer port is published; torrenting is outbound-only.
- With ProtonVPN/PIA, routing sets `PORT_FORWARD_ONLY=on` so Gluetun picks P2P-capable servers.
- Traefik cannot discover a shared namespace, so routed services get a file-provider route
  pointing at Gluetun. The file is removed when nothing is routed.

## Configuration

`services.torrentDownloader` uses camelCase YAML. Key defaults:

| Key | Default |
|---|---|
| `proxy` / `vpn` | `false` |
| `seeding.maxRatio` | `2.0` |
| `seeding.maxTimeMinutes` | `1440` |
| `seeding.minTimeMinutes` | `30` |
| `speed.uploadMbps` / `downloadMbps` | `10` / `0` (unlimited) |
| `limits.maxActiveDownloads` / `maxActiveSeeds` | `5` / `10` |
| `management.slow` | enabled, `1.0` Mbps, 30 min |
| `management.stalled` | enabled, 20 min |
| `management.zeroProgressMultiplier` / `recoveryPerMinute` | `4.0` / `0.5` |
| `management.errors.deleteAfterMinutes` | `30` |
| `management.storage.pauseDownloadsAtPercent` | `90` |
| `management.storage.cleanupSeedingAtPercent` | `85` |
| `management.storage.cleanupStrategy` | `oldest` (`oldest`, `newest`, `least_ratio`) |
| `management.storage.minSeedTimeMinutes` | `30` |

`services.slskd` defaults: enabled, `slskd` API credentials, `removeCompletedAfterImport: true`,
20 download/upload slots, 4-hour failed-transfer retention, 4 concurrent searches.

`monitoring.speedtest.minHealthyDownloadMbps` (default `8`) gates slow/stalled cleanup; `0`
disables the gate.

## Sharp edges

1. Transmission is always on; only `proxy` and `vpn` matter in its section.
2. All cleanup deletes files, including the UI Cancel action.
3. Only downloading torrents are health-checked.
4. Progress is floored for display, so 99.97% reads 99.9%.
5. Storage cleanup is outside the event pipeline: no re-search and no auto-resume.
6. `MaxActiveTorrents` is inert.
7. Speed limits are unit-ambiguous: Transmission and slskd convert MB/s differently.
8. Duplicate removal notifications are possible after cleanup.
9. Soulseek is not in the event log; only the page and music notifications show it.
10. The shares directory is created but unused.
11. Event hydration is bounded to 5,000 events, so very old torrents can be re-announced.
12. Storage min-seed time is measured from `AddedOn`, not actual seeding time.
