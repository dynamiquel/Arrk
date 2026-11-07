# Playlist Bridge: listening lists across services

Playlist Bridge is an evaluation-only plugin fixture, not a shipped Arrk feature. It mirrors playlist membership and ordering between accounts; it does not acquire missing audio files.

## Connect a music account

Choose the source and destination music services, sign in and select the account to link. An administrator enabling the plugin does not connect every household member. Each person grants their own account access. Check the account name before saving a connection.

## Preview a playlist sync

Select a source playlist and a destination. Preview reports additions, removals, unmatched tracks and ordering changes without writing them. Review that report before Apply. Scheduled runs use the last saved mapping; changing a playlist name does not change the mapping's stable identifier.

## Missing tracks are not downloads

A track with no match remains unmatched in the preview. Syncing does not request an album or download audio. Add missing music through Arrk's normal discovery and acquisition flow, then rerun the preview. A matching artist name alone is not enough to choose between live and studio recordings.

## Keep favourites in a mirror

Enable Preserve destination favourites to keep starred destination tracks during playlist reconciliation. This affects playlist membership only. It does not prevent the library file from becoming stale or being deleted by Arrk's media cleanup. Use an administrator media pin for that separate protection.

## Resolve a sync conflict

If both sides changed, preview the conflict before choosing source-wins or destination-wins. The choice can remove tracks from a playlist but does not delete their audio files. Pause the schedule while fixing a mapping to prevent repeated conflicting writes.

## Disconnect and revoke access

Pause the schedule and remove the linked account. Revoke the plugin's grant at the provider too. Disconnecting leaves existing playlists in place; it stops later synchronization. Removing Arrk's watchlist bookmark does not revoke a music provider's token.
