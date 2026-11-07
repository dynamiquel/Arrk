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
- The store records every event, whether or not anyone wants to be notified, until its
  retention window expires.
- Notifications are a subset: only enabled topics, formatted and delivered. A disabled
  notification never enters the inbox and is never sent externally.
- Public alerts target the `User` audience and can use shared channels. Movie/series-added,
  download and import alerts also reach followers through each user's own transports. Shared
  delivery does not depend on anyone following the title or on Discord DMs being configured.
- Subscriber-only and explicitly targeted alerts never leak into shared webhooks. Personal
  request decisions remain private; following a title adds delivery rather than replacing the
  configured public alerts.
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
                               | resolve additive audience selectors
                               | persist one inbox row per recipient
                               v
                    role endpoints and per-user channels
```

Audit persistence happens first; a notification failure is logged and the audit row is kept.

`NotificationMessage.Audience` combines independent selectors; no selector takes precedence:

| Selector | Inbox | Delivery |
|---|---|---|
| `Roles = ["User"]` | One owned row per active, approved account, including admins | Matching public routes; **not** a DM to everyone |
| Other roles, e.g. `Roles = ["Admin"]` | One row per active account in those named Identity roles | Matching role routes **and** those accounts' direct destinations |
| `UserIds = [7, 8]` | One row per eligible account | Those accounts' direct destinations |
| `FollowersOf = entity` | One row per matching eligible follower | Those accounts' direct destinations |

Role names are strings, so adding a role requires no dispatcher change. User, role, and follower
matches are unioned and deduplicated before delivery. Disabled, pending, and deleted accounts are
excluded. An empty audience addresses nobody. Messages default to `NotificationAudience.Everyone`,
which is simply `Roles = ["User"]`; there is no separate global flag or delivery path. Producers explicitly
select private audiences. `PublishToUserAsync` replaces all selectors with that one user.

Admins hold both `Admin` and `User` in Identity. Notification audiences use actual role membership,
including for `User`; there is no notification-specific alias for all accounts. The broad `User`
audience adds inbox/shared delivery, while narrower roles also select their members' direct clients.

Public media/download events use `NotificationAudience.Everyone with { FollowersOf = entity }`.
They send to matching public routes, then directly to matching followers, without duplicate inbox
rows. New requests target Admin; decisions target the requester. Neither becomes public implicitly.

Each distinct role produces a shared audience even with no current local members. A client's
`routing` list filters both topic and source role; every matching entry sends, in list order.
Duplicate topics and URLs are allowed. A route accepting multiple roles can send once for each
selected source role; route entries are subscriptions, not deduplicated destination addresses.
Personal alerts never match shared routes. Operators own Discord channel permissions and webhook
access control: source-role filtering is delivery configuration, not an external privacy guarantee.

Each inbox row aggregates successful clients and failures for the destinations relevant to its
owner. A failed DM or route does not erase an earlier successful send, and a successful send does
not erase a failure. A client exception is isolated so other clients and destinations still run.
Cancellation propagates after recording already-completed client outcomes, including earlier
routes of the interrupted client. Clients yield one outcome per endpoint rather than collapsing
fan-out into a misleading all-or-nothing result. This is synchronous, best-effort external delivery, not a
durable queue or exactly-once delivery system; inbox persistence precedes external sends.

Entity alerts carry a `MediaEntityRef`; the subscription service matches it against
`NotificationSubscription` rows. A subscription matches the exact item key, descendants when
`IncludeDescendants` is set (a series includes its seasons and episodes), or any ancestor key (an
episode matches a season or series follow). A topic that subscriptions never target, such as
`downloads.progress`, produces no subscriber rows at all.

## Where things live

- `Arrk.Notifications.Abstractions`: messages, audiences, client/service contracts, inbox and
  subscription records, styles, severity and transport-independent preferences. This small
  assembly lets Core audit records and domain/UI producers reference contracts without depending
  on the engine or creating a Core/UI/Notifications dependency cycle.
- `Arrk.Core/Events`: bus, audit store and `NotifyingEventStore`. Core maps notification tables
  in its shared database but does not register or implement the dispatcher or clients.
- `Arrk.Notifications`: `NotificationModule`, registry, topic policy/matching, audience resolution,
  inbox, subscriptions, per-user transport consent, retention and generic notification UI.
- `Arrk.Notifications.Discord`: its own module, options/validation, webhook and bot REST clients,
  embeds, account card and Events settings contribution.
- `Arrk.Notifications.Webhook`: its own module, options/validation and routed JSON webhook client.
- Each domain module: its own `INotificationEventProvider` (topics, defaults, emoji, colour,
  groups) and its producers.

`NotificationEventRegistry` aggregates providers at startup and throws on duplicate topics, so
the engine never references torrent, TV or media code. Transport modules depend on the engine,
never the reverse; the app composes them. A transport can be omitted without changing the inbox.
The Events page renders `notifications.settings` UI contributions without knowing about Discord.

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
cached destination for any future transport (Telegram, email, Web Push). Each client receives a
resolved `NotificationRecipient` and owns its account linking, opt-in and destination handling.

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
    username: Arrk
    avatarUrl: ""
    routing:                       # every matching rule sends
      - topic: media.tv.#
        url: "https://discord.com/api/webhooks/.../tv"
        # fromRoles defaults to [User], accepting public events
      - topic: media.requests.new
        url: "https://discord.com/api/webhooks/.../requests"
        fromRoles: [Admin]
      - topic: media.requests.new   # same event can go to another channel too
        url: "https://discord.com/api/webhooks/.../admin-audit"
        fromRoles: [Admin]
      - topic: system.#
        url: "https://discord.com/api/webhooks/.../system"
        fromRoles: [User, Admin]
    exclude:                       # delivery-only; still visible in-app
      - downloads.progress
    directMessages:
      enabled: true
      botToken: "..."              # secret; keep out of source control
      applicationId: "<APP_ID>"
      apiBaseUrl: "https://discord.com/api/v10"

  webhook:
    enabled: false
    authHeader: ""                 # e.g. "Bearer abc123"
    routing:
      - topic: system.#
        url: "https://n8n.example/system"
      - topic: media.requests.#
        url: "https://n8n.example/admin-requests"
        fromRoles: [Admin]

  topics:                          # hierarchical overrides
    system.health: true
    downloads.progress: false
    media.tv.*: true
```

`routing` is the only place to define shared endpoints. `topic` defaults to `#`, `fromRoles`
defaults to `[User]`, and `url` must be an absolute HTTP(S) URL. Names match case-insensitively.
A rule must match both the event topic and its selected source role. A public event does not
match an Admin-only rule just because admins can see it. Individual-user and follower-only
messages never match these rules. Empty lists or no matches skip shared delivery.

Routing has no winner or implicit default URL: use a `topic: "#"` entry for a catch-all; it sends
in addition to any more-specific matching entries. Duplicate rules remain distinct sends.
The old URL fields, dictionary routing, and `roleRouting` are removed rather than migrated.
`topics` enables or disables an event everywhere; `exclude` removes a topic from one transport,
including shared routes and DMs.

### Event retention

The durable audit log is trimmed daily by `EventPurgeJob`. The default applies to every event;
topic overrides keep noisy topics for less time. `job.#` defaults to 7 days because background
jobs produce start/finish audit rows continuously. `0` keeps that scope forever.

```yaml
events:
  retention:
    enabled: true
    defaultRetentionDays: 30    # all events without a topic override
    topicRetentionDays:
      job.#: 7                  # supports #/* (any depth) and + (one level)
```

Patterns use the same wildcard syntax as notification topics. The most specific matching pattern
wins; events matching a topic override are excluded from the default purge. The job runs at
`30 4 * * *`, after the inbox cleanup.

Transport settings are registered and validated by their owning modules at `notifications:discord`
and `notifications:webhook`; they are not properties on the engine's `NotificationConfig`.
All clients read monitored options on each send. Discord `enabled` controls both webhook and DM
delivery; `directMessages.enabled` additionally controls DMs. The generic webhook supports
matching role routes; individual user destinations are skipped.

## In-app inbox

Every enabled alert is stored with its topic and group, title, body, severity, deep link, source,
read state and delivery status. Public events create individual rows, so dismissing or reading an
alert never marks another user's alert as read. Mark/read/count operations require an account id
and updates check row ownership; unauthenticated UI never queries a shared inbox. Accounts created
later do not inherit historical public alerts. Legacy ownerless rows are excluded, with no
notification data migration. The Blazor UI consumes `INotificationService` server-side; there
is no dedicated REST API. Queries support category prefix, key, unread-only, search, since and
limit. `NotificationPurgeJob` trims the inbox daily; `EventPurgeJob` trims the audit log daily.

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

## Adding a client or event

Create an `Arrk.Notifications.<Transport>` module referencing `Arrk.Notifications`. Implement
`INotificationClient` with a stable `Name` and one
`SendAsync(NotificationMessage, NotificationRecipient, CancellationToken)` method. Register it as
an `INotificationClient` in that module and compose the module in the app (or plugin loader).
No dispatcher reference or switch is needed. Clients never resolve audiences or publish messages
back through the dispatcher. `SendAsync` returns `IAsyncEnumerable<NotificationDeliveryResult>`:
yield `Ok`/`Fail` after each endpoint completes, or `Skip` when disabled, filtered, unsupported,
unlinked, opted out, or unmatched. Use `[EnumeratorCancellation]` on the token in async iterators,
and propagate cancellation. Earlier yielded outcomes survive a later failure or cancellation.
Put options, validation, protocol retries, settings UI and external
identity resolution in the client module. Use `UserNotificationChannelService` for per-account
consent/destination state when needed. Use distinct stable client names for delivery reporting.

To add an event:

1. Add topic constants in your module.
2. Implement `INotificationEventProvider` and return definitions (topic, default, description,
   emoji, colour) and any curated groups.
3. Register it in `ConfigureServices`.
4. Attach a `NotificationMessage` to the `SystemEventRecord` you record. Set
   `Audience = NotificationAudience.Everyone with { FollowersOf = entity }` for public title events. Use
   `new() { FollowersOf = entity }` for follower-only alerts, `new() { Roles = ["Admin"] }` for
   role alerts, or `new() { UserIds = [userId] }` for private direct alerts.

The registry picks it up and throws at startup on duplicates.

## Tests

`tests/Arrk.Notifications.Tests` exercises audience composition, arbitrary roles, follower
preferences, account eligibility, public role delivery with no local accounts, private-recipient
isolation, independent read state, client failure isolation, topic policy and retention against
SQLite. `tests/Arrk.DownloadManagement.Tests` covers `EfEventStore` topic-aware purging and
`EventPurgeJob` retention mapping against SQLite. Transport tests use local HTTP endpoints and a fake Discord REST handler for protocol
errors, rate limits, DM identity caching, opt-outs, config reload and nested module settings.
Producer tests in Requests, TV, Downloads and Lifecycle assert the intended selectors. Live
Discord smoke tests remain explicitly environment-gated; ordinary tests never contact Discord.

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
