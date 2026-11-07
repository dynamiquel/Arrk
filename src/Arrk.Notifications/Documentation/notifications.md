# Events, notifications and Discord

Find recent activity, follow titles and choose which alerts reach you or a shared channel.

## Events versus inbox

Open the app-bar bell for live activity and unread notifications. Dismiss an item or use Mark all read to clear unread alerts. Open Events at `/events` for the wider activity history, with categories and search. Downloads shows activity relevant to transfers.

Events record what happened. Notifications are the alerts you choose to receive about some of those events. Turning off a notification does not erase the underlying event. Not every event has an alert, so use Events when investigating a missing message.

## Choose topics and delivery

A topic is a category such as downloads, requests or system health. Choose the topics you want in notification settings. Administrators can also configure shared Discord or webhook delivery. A webhook sends messages to a configured destination, such as a Discord channel or an automation service.

Start with a small set of useful alerts, then adjust after seeing the volume. Shared delivery settings do not mean every personal alert goes to a public channel. Retention limits how long inbox entries are kept; notifications are not a backup of your library or configuration.

For manual configuration, `notifications.topics` controls enabled categories. Discord and generic webhook settings can route categories to different destinations. A channel's `exclude` setting suppresses its delivery without removing the in-app alert. Leave `notifications.showJobActivity` off unless you need background-task messages for troubleshooting.

## Follow a title

Requesting a title follows it automatically. A series or season follow includes its episodes. Adding a title to a watchlist also follows it, so you can receive relevant updates without watching the Downloads page continuously.

Removing the last bookmark removes that watchlist's follow, but a separate manual or request follow can remain. Request decisions can still notify you after unfollowing because they answer your request; the follow's status preference controls those alerts.

Personal alerts go to the interested accounts and their direct delivery channels, not the global webhook. Expect important changes and problems rather than a direct message for every download percentage update.

## Enable Discord direct messages

You need a linked Discord account, a configured bot and a server shared with that bot. Your Discord privacy settings must allow messages from server members.

1. An administrator creates or chooses a Discord application and obtains its bot token from the Discord developer portal.
2. The administrator configures the bot under `notifications.discord.directMessages` and invites it to a server the intended users belong to.
3. Each user opens My account in Arrk and links Discord.
4. Open Events and use the Discord row to check the direct-message toggle and send a test.
5. If the test fails, check the shared server and Discord message permissions before trying again.

Unlinking Discord or turning direct messages off stops delivery. A Discord webhook for a shared channel is a different setup from the bot used for personal messages. Keep both webhook URLs and bot tokens private.

## Missing or noisy alerts

1. Look in Events for the original action and its time.
2. Check whether the topic is enabled and, for a title update, whether you follow it with the relevant categories enabled.
3. Check the intended destination: personal messages do not automatically go to the shared channel.
4. For Discord direct messages, use the test action and review its result.
5. Ask an administrator to check delivery settings, routing and exclusions if the in-app alert exists but the external message does not.

For noise, turn off categories you do not need or suppress a channel rather than losing all useful alerts. If an expected event is absent too, the action may have failed or may not yet produce that alert. Share the relevant error with private destination details removed.
