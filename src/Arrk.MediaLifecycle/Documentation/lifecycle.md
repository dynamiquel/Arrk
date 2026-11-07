# Watchlists, history, pins and storage cleanup

Save titles for later, review what you have watched and protect favourites before cleaning up space.

## Watchlist is not a download

Use watchlists on Movies or TV to organise titles you want to remember. Adding a title bookmarks it; it does not request or download it. Removing a bookmark does not delete its video file.

1. Open a movie or series and use its watchlist action.
2. Find saved titles in the Watchlists shelf on `/movies` or `/tv`.
3. Create or manage named watchlists from that shelf as needed.

Some lists come from an external service and are read-only. Change those at their source. A bookmark gives a library title a longer cleanup grace period, not permanent protection. Ask an administrator to pin media that should never be considered stale.

## History and the staleness clock

Open History at `/history` to review your playback. Administrators can select a user and filter or search the timeline. Arrk records playback through its own players and Jellyfin clients. External Navidrome-client listening is not attributed to an individual Arrk account in that history.

Stale means a downloaded title has gone unused for the configured time. New items age from when they were added; watching or listening resets the relevant clock. Playing an episode also refreshes its season, and playing a track refreshes its album.

For TV, cleanup considers downloaded seasons, not just individual episodes. Movies and music albums are considered individually. Unwatched media can become stale too; never assume it is protected merely because nobody has started it.

## Thresholds and protections

The defaults mark media stale after 30 days and allow 45 days for bookmarked video or music kept in playlists. Early warnings and automatic deletion are off by default.

- Watchlists and playlists extend the grace period; they do not protect forever.
- An administrator pin excludes an item from stale warnings and automatic cleanup.
- Pinning a series protects its seasons and episodes. A broader pin also protects the items beneath it.

Administrators can review the settings in Configuration under `monitoring.tv.stale` and `monitoring.music.stale`. `upperThreshold` sets days until stale; `keepThreshold` sets the longer grace period. `lowerThreshold` and `keepLowerThreshold` control early warnings, with zero disabling those warnings. `autoCleanup` permits automatic deletion for that library.

## Review before enabling deletion

Only administrators can manage cleanup at `/stale`. Before turning on automatic deletion, review what Arrk considers old and check that playback history reflects how your household actually watches or listens.

1. Open Stale and review the stale, about-to-be-stale and pinned tabs.
2. Check each title's remaining time and estimated space.
3. Pin favourites and anything you cannot replace.
4. Back up files you need to preserve before using one-off or bulk delete actions.
5. Observe the results over time before enabling `autoCleanup` for either library.

Automatic cleanup deletes only when explicitly enabled for that library. Watching or listening again resets age, and pins remain protected. Review season-level scope carefully before deleting TV content.

## Download cleanup is separate

Download cleanup removes old or unhealthy transfers according to seeding, speed and storage rules. Media cleanup removes library content according to age, playback and pins. Changing one policy does not change the other.

If space is low, first check whether the full disk holds downloads or library files. A paused download may need space freed and a manual resume; pinning a film does not solve a full download disk. For any proposed library deletion, check the exact titles and scope before confirming.
