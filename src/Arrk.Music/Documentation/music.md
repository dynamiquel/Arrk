# Music discovery, acquisition and listening

Find albums and artists, follow downloads and tell a short preview from a song in your library.

## Library and discovery

Music discovery uses Deezer for artist, album and track information. Arrk organises your music library, and Navidrome serves its audio for playback. A discovery result is not necessarily downloaded.

Open an album or artist to see its details and available actions. Album availability is based on its tracks: Partial means only some tracks are present. Adding an artist does not immediately download every album.

Only acquire music you are authorised to access. Before expecting a download, an administrator must configure a working source: torrents, Soulseek, or both. Soulseek is a file-sharing network accessed through the slskd service.

## Add music and follow acquisition

1. Find the artist or album in music discovery and open its details.
2. Check the artist and edition so you do not add a similarly named release.
3. Use the available add action, or request the item if your account needs approval.
4. For an album download, follow progress on Downloads and in the album's track list.
5. Wait for import to complete before playing the full album.

Album acquisition runs in the background. Arrk normally tries torrents first, then Soulseek; individual tracks generally use Soulseek. Administrators choose the sources with `services.music.useTorrents` and `services.music.useSoulseek`.

Music requests offer Best Available, Lossless and Lossy. Lossless preserves the encoded audio without further loss; lossy uses compression that discards some audio information. Adding or approving an artist request records the artist but does not immediately download its albums.

If Soulseek is unavailable, pending acquisition can wait until it recovers. Repeated failures can eventually leave an item waiting for an explicit retry. Check progress and Events rather than repeatedly submitting the same request.

## Import checks and failed sources

After downloading, Arrk checks that the files match the intended music before adding them to the library. A completed transfer may therefore still fail import. Files with the wrong album or an implausible track length are not accepted just because their names look similar.

Arrk excludes releases marked as mono, meaning single-channel recordings, from normal acquisition. An artist actually named Mono is not excluded. If a particular edition cannot be found, check whether that restriction applies.

A failed download source can be avoided temporarily while another is tried. That is different from permanently blocking a household request. AI ranking, when enabled, helps choose between suitable downloads; it does not remove file checks.

## Playback, previews and playlists

A Deezer preview is a short sample you explicitly play, not the full local song. Preview tracks do not enter album or playlist playback queues. Normal queued listening uses tracks already available in your library.

Open the album or playlist and use its playback actions. If tracks are missing from the queue, compare their availability with the downloaded tracks rather than assuming the player skipped files it has.

Playlists you own in Navidrome can be edited. External Deezer playlist subscriptions are read-only; change the source playlist rather than expecting to edit its contents in Arrk. Listening through Arrk records personal history. Listening in an external Navidrome client is not attributed to your individual Arrk account in that history.

## Lyrics and missing tracks

Lyrics come from a separate service and may not exist for every track or match every recording perfectly. A missing lyric does not mean the audio download failed. A genuine no-result is not retried repeatedly in the background; temporary service failures are treated differently.

For missing audio, check these in order.

1. Confirm it is a library track, not only a discovery result or preview.
2. Check whether the album was actually added or requested for download.
3. Look for an active or failed transfer on Downloads.
4. Check Events for a match or import error.
5. If the file imported but will not play, check Navidrome's health and library visibility.

Avoid moving or renaming library files as the first fix. Keep the relevant error and title details so an administrator can investigate without losing the original evidence.
