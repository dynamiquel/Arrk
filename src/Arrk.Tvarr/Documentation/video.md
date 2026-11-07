# Movies, TV, anime and local uploads

Find something to watch, request missing titles and add video files you already own.

## Discovery versus library

Open Discover to browse movies, shows and seasonal anime. Discovery is a catalogue of titles you might want; seeing a title there does not mean you have its video file. Movies at `/movies` and TV at `/tv` show your library.

Full discovery needs TMDb credentials configured by an administrator. TMDb supplies movie and TV information and artwork. Without those credentials, search through the library services still works, but discovery and anime results are limited.

Read the title's state before choosing what to do next.

- Downloaded means a movie has a file, or a series has every aired episode.
- Downloaded Monitored means every episode in the seasons selected for monitoring is present.
- Partial means some files are present; Missing means none are present.
- Downloading means a matching transfer is in progress, not that it is ready to watch.

Monitoring means asking the library service to look for missing or improved files. Specials are not counted in normal season progress.

## Add, request and search

1. Search or browse Discover and open the title's details.
2. Check the title, year and, for TV, season or episode scope.
3. As an administrator, use Add to Library and choose the available quality and search options. Other users can request the title for approval.
4. Follow the request on Requests or the transfer on Downloads. Wait for import before expecting playback.

An interactive search lets you inspect individual releases, the downloadable versions of a title. Rejected releases are hidden by default; read rejection reasons before overriding a choice.

For a library using only local files, an administrator can set `services.tv.search.enabled: false`. This hides search and retry actions but keeps upload available. It does not make your existing library disappear.

## Quality profiles

A quality profile sets the resolutions Arrk will accept and whether it should upgrade later. Choose the profile when adding or requesting a title. The defaults include Best Available, FHD for 1080p and UHD for 2160p.

Higher resolution is not always better: bitrate, the amount of video data per second, also matters. Default minimum bitrate settings are 3 Mbps for 1080p and 12 Mbps for 2160p. These affect future downloads, not removal of existing files. Upgrades use extra download traffic and storage.

Administrators manage profiles in Configuration under `qualityProfiles.profiles` and bitrate floors under `qualityProfiles.minimumBitratesMbps`. Setting a bitrate tier to zero leaves it unmanaged. Back up before changing the profile list: profiles removed from it can be removed from the library services too. Changes to an existing profile may not update that profile in those services; check there if the results do not match your settings.

## Upload a known title

You need an administrator account, a supported video file and enough free space for a temporary copy and the imported file. An import adds a file to the organised library; it is separate from uploading the bytes.

1. Open the matching title in Discover and choose Upload beside Add to Library, or open the title already in your library.
2. In the library, movie details offer Upload; series details offer Upload episodes; an episode menu offers Upload file.
3. Choose the video files and any separate subtitle files in the same batch.
4. Review the target and episode mapping, then confirm the import.
5. Wait for the imported file to appear in the library and player.

Arrk copies uploads into their final library folders. Successful temporary copies are normally deleted afterwards; failed copies can remain for retry until the configured retention period expires. Administrators can adjust limits under `services.tv.uploads` if needed.

## Auto-detect and correct a match

Use auto-detect upload on a library page when you do not want to select a title first. Arrk suggests a match and shows a score, alternatives and season/episode mapping. The score is a hint, not proof that the match is right.

1. Check the title and year, especially for remakes with the same name.
2. Review every episode mapping, particularly for anime using continuous episode numbers.
3. Choose another candidate or use manual TMDb search if the suggestion is wrong.
4. Enable the replace switch only if you intend to overwrite an existing file.
5. Confirm when the target is correct. Nothing imports before confirmation.

AI matching can help choose between suggestions when enabled, but your review still matters. Separate subtitle files are matched to the imported video and become available after the player library refreshes.

## Finished downloading but not playable

A completed transfer still needs to be imported and then discovered by Jellyfin, the video player service.

1. Check Downloads for completion or an error.
2. Check Events for import activity and any rejection or file-access problem.
3. Confirm the library title actually has a file, not just an entry.
4. Allow time for Jellyfin to refresh, then try playback again.

If import fails, check disk space, folder permissions and the file's match before downloading another copy. Administrators can inspect the underlying library service for more detailed import errors.
