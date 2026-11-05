# Music

How the native music module works. Status is in [`../plan.md`](../plan.md).

## Model

Deezer is the identity and metadata provider. Stackarr's SQLite database is the library.
Navidrome plays what Stackarr placed on disk. Prowlarr, Transmission and slskd fetch it.

| Concept | Meaning |
|---|---|
| Identity | Canonical Deezer id for artist, album or track |
| Discovery | Deezer charts, genres, editorial playlists and search (`MusicCatalogService`) |
| Library | What Stackarr tracks and holds (`MusicLibrary`) |
| Availability | How much of an album is on disk |
| Intent | "Add this" for an album, artist or track (`LibraryIntent`), replacing Lidarr monitoring |
| Grab | One acquisition attempt with the Deezer id it was requested for (`GrabRecord`) |
| Subscription | Read-only projection of an external Deezer playlist |
| Playlist | A Navidrome playlist the user owns and edits |
| Lyrics | Derived metadata keyed by track identity, stored as a sidecar |

Watch history and cleanup are in [`lifecycle.md`](lifecycle.md).

## Invariants

1. **One identity provider.** Deezer ids are canonical. Imports are always identified;
   `DeezerId <= 0` only exists for legacy rows placed before matching.
2. **One item shape.** `MusicItem` is the only shape the UI consumes. Discovery items are
   annotated with library state, so the same card and popup render everywhere.
3. **One popup per entity kind** (`AlbumDetailDialog`, `ArtistDetailDialog`,
   `PlaylistDetailDialog`), opened through `MusicDialogLauncher`.
4. **A playlist is a playlist.** Library, subscription and editorial playlists share one shell;
   only available actions differ. Subscriptions are read-only and excluded from "Add to playlist".
5. **Library membership beats name matching.** Match by Deezer id first, then normalised artist
   and title as a fallback for download-derived rows.
6. **Availability is per track.** `TrackFileCount` / `TotalTrackCount` drives Downloaded, Partial
   and Missing. A partially held album groups with Downloaded.
7. **Identify before placing.** Every completed grab is matched to a Deezer release before it
   touches the library; only identified releases land in `Artist/Album/NN - Title` with full tags
   and cover art. An unmatched release is not imported, so library and filesystem agree.
8. **No mono.** Results flagged mono (`(Mono)`, `Monaural`, `Mono Mix`) are excluded from every
   search, torrent and Soulseek. An artist or title that is itself mono (the band Mono) is kept.
9. **Previews never enter a queue.** A 30-second Deezer preview plays only when the user clicks
   that row, as a one-off. Album and playlist playback queue only tracks with a Navidrome id.
10. **Missing means monitored and absent.** An album that entered the library for one song (or a
    playlist auto-download) is created unmonitored with only that song wanted, so it reads
    Partial. A `LibraryIntent` marks a want.
11. **Every song shows its own progress.** A grab links by Deezer track id, falling back to the
    album grab for a whole-album torrent. Failed grabs are never shown as downloading.
12. **A failed source is blacklisted and re-searched by identity.** The blacklist is per request
    (track or album) and expires after `services:slskd.blacklistHours` (default 168). Re-acquire
    carries the requested artist from the Deezer track id, never a bare title.
13. **A single downloaded song must be that song.** A one-file import is probed; if its duration
    is more than the smaller of 10 seconds or 10% from the Deezer length, it is rejected and
    re-acquired. A one-file grab binds only to the exact requested track.
14. **One persisted acquisition queue.** Every request writes a `MusicAcquireQueueItem`; the
    drain job processes a batch least-failed first, then oldest. A failed attempt falls behind
    healthier requests. After 50 attempts the request parks until an explicit action re-arms it.
    A row in flight for more than six hours is retried; each attempt is bounded by 45 seconds, and
    the run is skipped while Soulseek is unavailable.
15. **A release should be the album, not merely share its words.** Before placing a torrent, its
    files must share at least one distinctive title word with the resolved album; otherwise the
    grab fails as `music-mismatch` and is re-queued. Number-only rips are placed positionally.
16. **Lyrics are derived and a miss is final.** LRCLIB has no ISRC/MBID/Deezer id, so matching is
    fuzzy by design. A track with no lyrics is `NotFound` and is not retried on a timer; a
    failure on Stackarr's side is `Failed` and retried by the daily reset.
17. **A provider is a service, not an oracle.** The LRCLIB client retries transient statuses and
    honours `Retry-After`; only `404` means not found. A sweep stops early when the provider is
    clearly down.

## Adding music

`AddAlbumToLibraryAsync` / `AddArtistToLibraryAsync` write library rows from Deezer metadata,
record `LibraryIntent`, notify, then enqueue a background grab. The UI never blocks on an indexer
search.

## Acquisition

`MusicAcquisitionService` searches Prowlarr title-first in audio categories (`3000-3999`) and
scores candidates, or asks slskd for Soulseek candidates.

- Albums prefer torrents, with Soulseek as fallback.
- Single tracks are Soulseek-only; the indexer is used only when Soulseek is disabled or
  unavailable.
- Every search uses the cleaned title, with Deezer's disambiguation tails stripped.
- Every grab emits a notification; a search that grabs nothing records an audit-only
  `music.release.missed` event explaining why.
- `services:music.useTorrents` and `useSoulseek` choose the sources (both default true).

Optional AI release ranking (`ai.features.releaseRanking`) may choose among candidates that
already passed the deterministic matcher. `shadow` only records, `ambiguous` acts only on ties or
edition markers, and `always` picks among the top N. It never changes rejection rules, the
blacklist or import verification.

## Import

A grab only stages files: torrents under `/downloads/complete/music`, Soulseek under
`/downloads/slskd`. Import then identifies the release and places it.

- Identity comes from the persisted Deezer id, then the held album, then embedded tags, then a
  Deezer search.
- Files land in `music/<AlbumArtist>/<Album>/<NN - Title>.<ext>`, with `<D>-<NN - Title>` for
  multi-disc albums. Torrent files are hard-linked so seeding continues; Soulseek files are moved.
- Each file is tagged with artist, album artist, album, title, track, disc, date and
  `STACKARR_DEEZER_*` ids, so it can be re-identified without the database. The Deezer cover is
  written as `cover.jpg`.
- Files that do not map to a track are skipped.
- A completed torrent that clearly belongs to another album is not placed; it is kept so the
  album it really is can reuse it.

## Playlists

Three sources share one concept: the user's Navidrome playlists, subscribed Deezer playlists
(read-only) and Deezer editorial playlists. Discovery surfaces the editorial ones. Every playlist
opens the same popup; only available actions differ.

Subscription auto-download queues only songs missing from Navidrome and records when each was last
tried. A retry cooldown rotates the next sync to the next batch, so a sync cannot flood the
acquisition queue.

## Lyrics

A background sweep (`MusicLyricsJob`, every 30 minutes) takes tracks that have a file and no
lyrics, asks LRCLIB, verifies the match and writes a sidecar (`.lrc` for line timings, `.txt` for
plain). Stackarr then asks Navidrome to rescan.

Queries widen from an exact signature to structured search, then title alone, then free text. A
candidate is accepted only when artist and title agree and known durations are close. Placeholder,
cover and karaoke credits are filtered out. The weaker acceptance tier also requires the album.

State lives on `MusicTrack`: `None` (eligible), `Found`, `Instrumental`, `NotFound` (final) and
`Failed` (Stackarr-side failure, retried daily). An unreachable provider leaves the track `None`
and bumps `LyricsCheckedAt` so it rotates behind the queue.

Playback never matches anything: Navidrome serves the sidecar by file association, and
`/api/music/player/lyrics/{songId}` proxies the enhanced OpenSubsonic call so the browser never
holds Subsonic credentials. Hits are cached; misses are rechecked.

The renderer does all per-frame work in JavaScript off the audio clock. Blazor hands it a model
once per track and never renders per frame, because that would saturate the SignalR circuit. The
bar's in-place lyrics pane is desktop-only; phones use the full-screen player's lyrics sheet.

## Player

The mini player and the full-screen now-playing view are two surfaces of one playback: one
`<audio>`, one queue, one OS media session. The full view opens from a chevron strip on the bar
and can be closed with the matching strip, a close button or Escape. Desktop shows art and an
optional lyrics side pane; phones slide lyrics over the art and hide the volume slider.

The bar and full view survive navigation because the overlay is mounted with the layout. Opening
the video player tears the music component down, so the video player stops music first; the music
player rebinds its DOM on every mount but keeps the existing `<audio>` element and its listeners.

## Performance

Opening an album must stay fast: ffprobe results are cached per album/track and cleared only when
files are imported; per-track probes run with bounded concurrency; membership and library views
use a short TTL invalidated on writes; a Soulseek search stops as soon as slskd marks it complete.

## Conventions

- New music UI uses `MusicCard` with `.st-music-grid`; popups go through `MusicDialogLauncher`.
- New acquisition writes go through `MusicLibrary`; jobs and UI never write the database directly.
- Lyrics work goes through `LyricsService` as a background sweep or manual request, never inline
  from a page load or the importer.
- Karaoke rendering stays in `stackarr-lyrics.js`.
- No MusicBrainz, no Lidarr, no second player element and no second lyrics renderer.
