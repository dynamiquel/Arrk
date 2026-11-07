# Video playback and subtitles

Watch an imported title, choose audio and subtitles, and work through common playback problems.

## Play and resume

Before playing, the title needs a video file in the library and must be visible to Jellyfin, Arrk's video player service. A discovery result or approved request is not enough.

1. Open the movie or episode and choose Play.
2. Use Continue Watching to return to an unfinished video or the next episode of a series.
3. Choose audio and subtitles from the player's track menus if needed.

Continue Watching is personal to your account and shows one card per series. Near-end viewing can count as finished, so the next episode may appear even if you did not watch every credit.

Near the end of an episode, autoplay shows a next-episode card. Play now advances immediately. Dismissing the card, seeking back or pressing Escape cancels the countdown.

Administrators can use `services.jellyfin.webPlayer.enabled` to choose between Arrk's in-site player and Jellyfin's own web client. Turning it off does not remove Jellyfin or the library.

## Audio and subtitle selection

Open the player's track menus to choose an available language or subtitle track. Changing audio may briefly restart the stream at your current position.

Text subtitles can usually switch quickly. Image-based subtitles, such as PGS from a disc, may need to be burned into the video. That means the server draws them into each frame and can require more processing power. Different subtitle tracks can therefore have noticeably different performance.

Use the subtitle appearance menu for text size, background opacity, outline and style. Those preferences are saved in your browser. Opening title details from the player pauses playback; it resumes afterwards only if it was playing before.

## Get missing subtitles

An administrator must enable subtitles and configure at least one provider, the service supplying subtitle files. The built-in OpenSubtitles provider requires your API key. Bazarr is another, optional service for additional providers; it is not required for OpenSubtitles.

1. In Configuration, enable subtitles and choose languages under `services.subtitles`.
2. For OpenSubtitles, enter `services.subtitles.opensubtitles.apiKey`; add account credentials if you want to use the account's quota.
3. Review automatic download and score settings. A minimum score filters weaker matches; it does not guarantee perfect timing.
4. For older library items, use Get missing or a manual subtitle search from the item's available subtitle controls.

When automatic download is enabled, new imports are checked for missing languages. Existing subtitles inside the video can already satisfy language coverage. Separate subtitle files stored beside the video also count. If no provider is configured, subtitle controls may be hidden.

Provider download quotas still apply. Repeated attempts do not reset them. If using Bazarr, leave its independent wanted search out of the way so it does not compete with Arrk's automatic searches.

## Stream or transcode problems

Transcoding means converting video or audio while it plays so your browser can handle it. Jellyfin performs that work. A file can play smoothly with one audio/subtitle choice but require conversion with another.

1. Check that Jellyfin is healthy and the title is present in its library.
2. Try another available audio track or text subtitles instead of image subtitles.
3. Try another supported browser to narrow down a compatibility issue.
4. Check the server's available CPU or GPU capacity and relevant errors in Events or logs.

NVIDIA acceleration requires a compatible GPU and working host support. Administrators can enable it with `services.jellyfin.nvidia`; turning the setting on cannot supply missing hardware or drivers.

For support, describe the browser, title, selected tracks and error. Do not share signed playback URLs or player access tokens; they can grant access to media.
