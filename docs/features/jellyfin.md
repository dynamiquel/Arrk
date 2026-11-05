# Jellyfin in-site web player

How the `jellyfin` web player is implemented.

## Model

Jellyfin negotiates and produces the stream. Stackarr owns the page, controls and proxy, so a
browser can play any library item without seeing a Jellyfin URL or token. Stackarr does not
reimplement transcoding.

`/watch/{itemId}` renders a bare `<video>` and asks Jellyfin for a playable HLS stream using a
device profile composed from the browser's capabilities.

| Layer | Responsibility |
|---|---|
| `Watch.razor`, `PlayerLayout.razor` | Page parameters, bare layout, controls and track menus |
| `stackarr-player.js` | Capability probe, HLS attach, track selection, progress/completion, autoplay |
| `JellyfinPlaybackService` | PlaybackInfo negotiation, device profile, track model, signed URLs, next episode |
| `PlaybackEndpoints` | Start/stop/progress/complete/next/image endpoints and the stream proxy |
| `HlsPlaylistRewriter` | Strips Jellyfin's `ApiKey` from playlists |

## Provisioning

Stackarr completes the Jellyfin wizard, creates the Movies, TV Shows and Music libraries, links
the `/movies`, `/shows` and `/music` mounts and keeps their metadata on TMDb. Configuration is
idempotent and re-applied on a schedule.

`JellyfinLibraryScanJob` refreshes every five minutes and immediately on `MediaImportedEvent`.

With `services.jellyfin.nvidia` on, the container gets the NVIDIA runtime and all GPUs, and
Jellyfin is set to `nvenc` with hardware encoding and HDR tone mapping. The runtime flag alone
only exposes the GPU.

The OIDC RBAC plugin is configured with Stackarr's issuer, client id and current secret. When the
secret rotates, the installer rewrites the configuration and restarts Jellyfin.

## Device profile

`JellyfinDeviceProfileFactory` is a pure function over `BrowserCapabilities`:

- The browser probes HLS, HEVC, AV1, VP9, FLAC, Opus, AC3 and EAC3; Stackarr never guesses from a
  User-Agent.
- MKV is never advertised as direct play. Output is HLS fragmented MP4; Jellyfin copies when it
  can and re-encodes only when it must.
- Text subtitles are declared External, including ASS/SSA. Image subtitles are absent, so
  Jellyfin can only burn them in.

## Track selection

- Always send `MediaSourceId` with explicit audio/subtitle indexes; Jellyfin ignores stream
  indexes without a source id.
- Text subtitles are side-loaded through a WebVTT proxy route and switched instantly. Negotiation
  requests `SubtitleStreamIndex = -1` so nothing is burned in by default.
- Image subtitles (PGS/VOBSUB) burn in; selecting one renegotiates at the current position.
- Audio changes renegotiate at the current position; the old transcode is stopped.
- `startTicks` carries the resume position through renegotiation.

## Continue Watching and autoplay

Jellyfin's `Resume` list returns every half-watched item, while `NextUp` returns one next episode
per series. Stackarr merges both into one streaming-style shelf:

- One card per series. In-progress episodes group by provider ids; the most recently played wins,
  then the furthest season/episode. If a later episode has been played, NextUp wins.
- Near-end counts as finished: episodes use 5% of runtime clamped to 20–60 seconds; films use 8%
  clamped to 1–3 minutes. This matters because the in-site player saves progress through
  `UserData`, which Jellyfin does not auto-promote to played.
- A finished episode resolves to the next episode in series order, skipping specials.
- The watch index carries per-user `UserData` and is cached per username for 30 seconds, so
  played state never leaks across accounts.

Autoplay prefetches the next episode, shows a card in the last ten seconds with still, title and
`SxxExx`, and plays in place without reloading or leaving fullscreen. Showing it before the stream
ends also avoids Jellyfin's tail loop. Autoplay updates the URL, title, header and deep link, so a
refresh continues on the right episode. Play now skips the countdown; seeking back, closing or
Escape cancels.

## Details, subtitles and proxy

- Player chrome: the title, episode line and artwork sit bottom-left above the scrubber.
- Title details: the player publishes the current item through `MediaDetailLauncher`; TV resolves
  it to the normal movie/series dialog. Playback pauses while the dialog is open and resumes only
  if it was playing.
- Subtitle appearance: a menu controls text size, background opacity, outline and style, stored
  in `localStorage`. Cues are lifted above the control bar by the VTT proxy; cues with their own
  line are left alone.
- Proxy: path-preserving, injects the Jellyfin token server-side. The URL carries a signed token,
  so an AirPlay receiver without cookies can fetch playlists and segments; a signed-in browser can
  fall back to its cookie. The proxy rejects absolute URLs and `..` traversal, strips `ApiKey`
  from playlists and sends `Cache-Control: no-store`.
- Sessions live in memory, are owned by the user who created them, refresh their idle timer on
  every stream request and are reaped after six hours.

## Configuration

```yaml
services:
  jellyfin:
    webPlayer:
      enabled: true
```

When the player is disabled, Play buttons open Jellyfin's own web client.

## Tests

`tests/Stackarr.Jellyfin.Tests` covers device profile, playlist rewriting, session store,
negotiation rules, Continue Watching merge, next-episode ordering and real-container playback
integration. `JellyfinPlaybackLiveTests` is an optional smoke test against a real library when
`STACKARR_LIVE_JELLYFIN_URL` is set.
