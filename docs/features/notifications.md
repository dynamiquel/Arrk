# Notifications and events

Arrk has three related mechanisms with separate lifetimes: an in-process event bus, a durable
event store, and user-facing notifications.

| Concern | Purpose | Lifetime | Consumer |
|---|---|---|---|
| `IEventBus` | Fire-and-forget reactions to a fact | Ephemeral | Internal services |
| `IEventStore` | Append-only audit log of everything notable | SQLite | Activity feed, debugging, notifications |
| `INotificationService` | Opted-in user-facing alerts projected from events | SQLite inbox + delivery | Users (in-app, Discord webhook/DM, webhook) |

Rules that keep them separate:

- The bus never persists and is never used for user-facing alerts.
- The store records every event, whether or not anyone wants to be notified.
- Notifications are a subset: only enabled topics, formatted and delivered. A disabled
  notification never enters the inbox and is never sent externally.
- Broadcasts go to everyone and can use the global channels. Entity alerts go only to users who
  subscribed to that title, and are delivered through each user's own transports.
- Targeted alerts never leak into the global webhook. That separation is what lets the global
  channel stay useful while personal notifications remain quiet.
- Producers write one `SystemEventRecord`. If it carries a `Notification`, `NotifyingEventStore`
  projects it into the notification pipeline. No producer talks to Discord directly.

## Flow

```
producer -> SystemEventRecord { ..., Notification = NotificationMessage }
              |
       NotifyingEventStore --persist audit--> SystemEvents
              |
              +--dispatch--> NotificationService
                               | evaluate policy
                               | resolve audience (broadcast/users/subscribers)
                               | persist one inbox row per recipient
                               v
                    global channels or per-user channels
```

Audit persistence happens first; a notification failure is logged and the audit row is kept.

`NotificationMessage.Audience` decides who receives the alert:

| Audience | Inbox | Delivery |
|---|---|---|
| `Broadcast` | One row visible to everyone | Every configured global channel |
| `Users` | One row per account in `AudienceUserIds` | Only those accounts' direct channels |
| `Subscribers` | One row per matching subscriber | Only those accounts' direct channels |

Entity alerts carry a `MediaEntityRef`; the subscription service matches it against
`NotificationSubscription` rows. A subscription matches the exact item key, descendants when
`IncludeDescendants` is set (a series includes its seasons and episodes), or any ancestor key (an
episode matches a season or series follow). A topic that subscriptions never target, such as
`downloads.progress`, produces no subscriber rows at all.

## Where things live

- `Arrk.Core` (`Events/`, `Notifications/`): bus and store, `NotifyingEventStore`, the
  notification model, `NotificationEventRegistry`, policy, inbox service, entity, purge job,
  subscriptions, per-user channel consent and `NotificationModule`.
- `Arrk.Notifications`: the delivery module with Discord webhook/DM and generic-webhook
  channels, plus the inbox, All Events page and notification settings UI. It depends only on Core
  and the auth contracts.
- Each domain module: its own `INotificationEventProvider` (topics, defaults, emoji, colour,
  groups) and its producers.

`NotificationEventRegistry` aggregates providers at startup and throws on duplicate topics, so
the delivery module never references torrent, TV or media code.

## Topics

Topics are stable dotted strings that filter and group by prefix:

| Group | Examples |
|---|---|
| System health | `system.health.service.down`, `system.health.storage.low`, `system.health.network.speedtest.degraded` |
| Downloads | `downloads.torrent.added`, `downloads.torrent.error`, `downloads.progress`, `downloads.imported` |
| TV and movies | `media.tv.added`, `media.tv.deleted`, `media.tv.subtitle.downloaded` |
| Music | `media.music.album.added`, `media.music.song.imported`, `media.music.lyrics.found` |
| Requests | `media.requests.new`, `media.requests.approved`, `media.requests.denied` |
| Lifecycle | `media.lifecycle.about_to_be_stale`, `media.lifecycle.stale`, `media.lifecycle.deleted` |

The authoritative list is the set of provider constants in each module. A topic can be registered
before its producer is wired; the catalogue is the vocabulary, not a promise that every topic is
emitted. See [`../plan.md`](../plan.md#implementation-status) for known gaps.

`NotificationEventRegistry` also curates groups so the settings UI shows a few toggles rather
than every event.

## Subscriptions and direct messages

`NotificationSubscription` is the per-user interest model. It stores the provider key, domain,
item key, title/art snapshot, source (`request` or `manual`) and five category switches:

| Category | Topics |
|---|---|
| Status | `media.requests.*` |
| Download | `downloads.torrent.added`, `downloads.completed`, `downloads.ready` |
| New content | `downloads.imported`, `media.tv.added`, `media.tv.upgraded`, grabs |
| Problems | `downloads.failed`, torrent error/stalled/slow/struck |
| Lifecycle | `media.lifecycle.*`, `media.tv.about_to_delete`, `media.tv.deleted` |

`downloads.progress` is deliberately absent: percentage updates stay in the in-app feed and are
never sent as DMs. A user gets started, finished, imported, problem and deletion alerts only.

Requests auto-subscribe the requester to the requested entity. A series request includes every
future season/episode; a season request includes its episodes; a movie or episode request is
exact. `IMediaRequestHandler.ResolveEntity` owns the provider-id-to-entity mapping, so the
Requests module never learns what a TMDB or TVDB id means. Deleting, denying, blacklisting or
cancelling a request removes its request-sourced subscription. Request decisions still arrive if
the user unfollows the title, because they answer a request the user made; the subscription's
status toggle is what suppresses those decision alerts.

`MediaEntityRef` travels with events from TV, downloads and lifecycle. Download management
resolves a torrent hash through the contributed `IMediaDownloadEntityResolver` (the TV module
uses the Radarr/Sonarr queue); an unmatched torrent stays a normal broadcast. Import events carry
an exact episode/season/series entity, so a series follow receives future episodes and seasons.

### Discord direct messages

Discord DMs use the bot REST API, not a gateway or SDK:

1. `POST /users/@me/channels` with `recipient_id` opens (or returns) the DM channel.
2. `POST /channels/{id}/messages` posts the styled embed.
3. The channel id is cached on `UserNotificationChannel` per user, scoped to the linked Discord
   identity so a re-link cannot reuse the old account's DM.

Setup:

1. Create or reuse a Discord application at `discord.com/developers/applications`.
2. On **Bot**, reset/copy the token and put it in
   `notifications.discord.directMessages.botToken`.
3. Invite the bot to a server your users are in:
   `https://discord.com/oauth2/authorize?client_id=<APP_ID>&scope=bot&permissions=0`.
4. Users link Discord under **My account**. Direct messages are on by default when the bot is
   active; the compact Discord row on **Events** shows the toggle and a test button.

Discord only allows a bot to DM a user it shares a server with, and the user must allow direct
messages from server members. A failure returns Discord's `50007`/`403` and the UI explains that
constraint. A future phase can add a user-install handshake for users with no shared server.

Only linked users receive DMs. Unlinking Discord or turning the toggle off stops delivery
immediately. The transport is channel-agnostic: `UserNotificationChannel` holds the consent and
cached destination for any future transport (Telegram, email, Web Push), and
`INotificationChannel.SendToUserAsync` is the only contract a transport implements.

Bookmarking a title in a watchlist is also a follow: adding an item creates a
`watchlist`-sourced subscription, and removing the last bookmark for that title removes it. A
manual or request follow survives bookmark removal. Series and season bookmarks include their
children, so future episodes and seasons are included.

Every external message identifies the instance. Discord embeds use the stack name as the author
(linked to the public URL) and include both in the footer; the generic webhook adds a `stack`
object with `name` and `url`, and relative deep links are made absolute.

## Configuration

Users toggle topic patterns; the most specific match wins, and unset topics fall back to their
catalogue default. A key is a plain prefix (`downloads`) or a wildcard pattern where `+` matches
exactly one level and `#`/`*` matches any depth.

```yaml
notifications:
  retentionDays: 90
  showJobActivity: false

  discord:
    enabled: true
    webhookUrl: "https://discord.com/api/webhooks/..."
    username: Arrk
    avatarUrl: ""
    routing:                       # per-category webhooks; most specific wins
      media.tv.*: "https://discord.com/api/webhooks/.../tv"
      media.music.*: "https://discord.com/api/webhooks/.../music"
      system.*: "https://discord.com/api/webhooks/.../system"
    exclude:                       # delivery-only; still visible in-app
      - downloads.progress
    directMessages:
      enabled: true
      botToken: "..."              # secret; keep out of source control
      applicationId: "<APP_ID>"
      apiBaseUrl: "https://discord.com/api/v10"

  webhook:
    enabled: false
    url: ""
    authHeader: ""                 # e.g. "Bearer abc123"
    routing:
      system.#: "https://n8n.example/system"

  topics:                          # hierarchical overrides
    system.health: true
    downloads.progress: false
    media.tv.*: true
```

Both channels resolve their target per notification: the most specific routing pattern wins, then
the channel's default URL. `topics` enables or disables an event everywhere; `exclude` removes a
topic from one transport only.

## In-app inbox

Every enabled alert is stored with its topic and group, title, body, severity, deep link, source,
read state and delivery status. The Blazor UI consumes `INotificationService` server-side; there
is no dedicated REST API. Queries support category prefix, key, unread-only, search, since and
limit. `NotificationPurgeJob` trims the inbox daily.

- The app-bar bell is a unified inbox: live activity first, then unread notifications, with a
  badge, per-item dismiss and "Mark all read".
- `/events` is the complete audit feed with category toggles, search and infinite scroll.
- The Downloads page renders the same feed scoped to download events.
- Background job runs are opt-in via `showJobActivity`, so recurring jobs do not fill the inbox.

## Discord styling

Embeds are styled per topic, not generically: an emoji prefix, an accent colour (red failures,
green recoveries/completions, orange warnings, purple media, teal users), an author line and a
footer naming the group. A producer can override style per message and attach artwork. The
webhook payload includes the resolved topic, group, domain, emoji and colour.

Discord only fetches absolute HTTP(S) URLs, so relative proxy paths are dropped. Producers supply
absolute art (TMDb, arr remote posters, Deezer CDN, Cover Art Archive).

## Adding a channel or event

To add a channel, implement `INotificationChannel` (`Name`, `IsConfigured`, `SendAsync`) and
register it in DI; `NotificationService` discovers all registered channels. A channel that can
reach one account also sets `SupportsDirectDelivery` and implements `SendToUserAsync`, reading
the user's consent and destination from `UserNotificationChannelService`.

To add an event:

1. Add topic constants in your module.
2. Implement `INotificationEventProvider` and return definitions (topic, default, description,
   emoji, colour) and any curated groups.
3. Register it in `ConfigureServices`.
4. Attach a `NotificationMessage` to the `SystemEventRecord` you record. Set `Entity` and
   `Audience = Subscribers` when the event belongs to one title; use `AudienceUserIds` for a
   direct alert.

The registry picks it up and throws at startup on duplicates.

## Import notifications

There is no arr webhook, so `MediaImportMonitor` polls Radarr and Sonarr history every minute for
`downloadFolderImported` records. Each import becomes a `downloads.imported` notification with
poster, fanart and a `MediaEntityRef` (movie, episode, season or series). A season pack is
announced once as an episode range. The checkpoint is derived from recorded events, so restarts
do not re-announce; with no checkpoint, only the last two hours are replayed.

`MediaImportedEvent` carries the same entity on the in-process bus. The Requests module listens
and marks a matching approved request `Available`, which produces the `media.requests.available`
alert. A series request matches its first imported episode/season; a season request matches any
episode in that season; movie/episode requests match exactly. Old approved requests without an
entity key are ignored rather than guessed at.
