# Requests

Stackarr's in-app request and approval system. TV and music fulfilment is covered in
[`tv.md`](tv.md) and [`music.md`](music.md).

## Model

A request is a persisted "please add this" record. Stackarr moderates it, then hands it to the
owning library module. `Approved` means the backend accepted the request, not that the media is
ready.

The Requests module owns no container and no media backend. TV and music contribute
`IMediaRequestHandler` implementations through `MediaRequestRegistry`, so the request system
never references Radarr, Sonarr or Deezer directly.

```
user -> RequestTarget { domain, kind, provider ids, scope }
             |
       RequestService (SQLite)
             | approve
             v
      IMediaRequestHandler
        /             \
  Tvarr (arrs)     Music (library + Deezer)
```

`MediaRequest` stores the requester (denormalised username), timestamps, domain, kind, provider-id
dictionary, display title/year/poster, scope, handler key, quality key/name, status, decision
audit and the library id assigned on approval. Requests have no foreign keys, so deleting an
account leaves history intact.

`RequestBlacklist` rows carry domain, provider ids, title, reason and who/when. There is no scope,
expiry or un-blacklist operation. Matching requires the same domain and at least one same-named
provider id with an equal value.

**Provider ids are identity.** `ProviderIdMap` serialises and compares the dictionary: blank keys
and values are dropped, invalid JSON becomes empty, and overlap means the same key with an equal,
case-insensitive value. Title, year and poster are display data only.

## Statuses

| Status | Meaning |
|---|---|
| `Pending` | Waiting for moderation, or about to auto-approve |
| `Approved` | Backend accepted the add/search; not necessarily available |
| `Denied` | Rejected by an admin |
| `Blacklisted` | Pending request that was blacklisted |
| `Available` | Defined but never assigned |
| `Failed` | Approval was attempted and the backend failed |
| `Cancelled` | Owner revoked, or the media was removed |

Only `Pending` can be approved, denied or revoked. `Denied`, `Blacklisted`, `Cancelled` and
`Failed` are terminal. Nothing deletes a request row.

## Creation gates

Before the row is saved:

1. `requests.enabled` is true.
2. The kind is allowed (`allowSeriesRequests`, `allowSeasonRequests`, `allowEpisodeRequests`;
   movie and music kinds are always allowed).
3. A handler exists for `(domain, kind)` and is available.
4. The target is not blacklisted.
5. `CheckAsync` reports it is not already in the library (existence, not file presence).
6. The user has no `Pending` request for the same domain, kind, season and episode with
   overlapping provider ids.
7. `maxOpenPerUser` is not exceeded (`0` is unlimited).

Auto-approval matches `autoApproveUsers` by username or `autoApproveRoles` by role. It is decided
after the row is saved and does not count against the open-request limit.

## Approval, denial and removal

`ApproveAsync` re-resolves the handler by `(domain, kind)`, picks the quality key (admin override,
then stored key, then `requests.defaultQualityProfile`), and calls `FulfilAsync` with
`SearchNow = true`. Success sets `Approved`, stores the library id and notifies the requester;
failure or exception sets `Failed` and records an audit event. There is no retry method and no
transaction with the backend.

- **Deny** (pending only) sets `Denied`, optionally writes a blacklist row when
  `blacklistOnDeny` is true, and notifies the requester.
- **Blacklist** works in any status; a pending request becomes `Blacklisted`, others keep their
  status.
- **Revoke** (pending, owner only) sets `Cancelled` without notification.
- **Media removal:** deleting a movie or series publishes `MediaRemovedEvent`; a hosted listener
  cancels overlapping requests except `Denied`/`Cancelled`. Music removals do not publish the
  event, so music requests are not reconciled.

## Handlers

Exactly one handler may own a `(domain, kind)` pair; duplicates throw at startup.

### TV (`TvarrRequestHandler`, key `tv`)

| Kind | Identity | Fulfilment |
|---|---|---|
| Movie | `tmdb` | Add to Radarr with the chosen/default profile, then search |
| Series | `tvdb` | Add to Sonarr, monitor all/future seasons, then search |
| Season | `tvdb` + season | Ensure the series, monitor that season, search |
| Episode | `tvdb` + season + episode | Ensure the series, monitor that episode, search |

`CheckAsync` only asks whether the series exists, so seasons or episodes of an existing series
are reported as already in the library and cannot be requested.

### Music (`MusicRequestHandler`, key `music`)

Capabilities: `(Music, Artist)`, `(Music, Album)`, `(Music, Track)`. Quality options are `0`
Best Available, `1` Lossless, `2` Lossy. Identity is a Deezer id (`deezer` for albums/tracks,
`deezerartist` for artists); album and track requests may fall back to a Deezer title search, and
an unidentifiable request cannot be fulfilled.

Artist requests add metadata and a library intent but start no download. Album requests add the
album, record the intent and enqueue acquisition. Track requests fulfil by adding the whole album;
no UI creates them.

## Blacklist vs failed downloads

Three mechanisms share the word:

| Mechanism | Scope | Lifetime | Blocks |
|---|---|---|---|
| `RequestBlacklist` | Domain + provider-id overlap | Permanent | Future requests |
| Arr release blocklist | One release/history entry | Managed by arr | Re-grabbing a failed release |
| Music source blacklist | One grab | Config window or process | A failed torrent/Soulseek source |

They do not interact: a failed download never blacklists the title from requests, and a request
blacklist never stops an arr retry.

## UI

`/requests` is visible to every authenticated user and shows all users' requests. It has five
summary tiles (Open, Accepted, Denied, Blacklisted, Total), filter tabs (Pending, Approved, Denied,
Blacklisted, Cancelled; `Failed` has no tab), and cards with poster, status, type, scope, quality,
requester and relative time. Admins can accept, deny or blacklist; owners can revoke their own
pending request.

Requests are raised from TV Discover, remote detail dialogs, and music album/artist dialogs.
Admin flows add media directly instead of creating a request.

## Notifications

`media.requests.*` topics cover new, approved, denied and blacklisted. Decision notifications go
to the requester's inbox; external channels still receive every accepted message.
`media.requests.available` is registered but never emitted. Routing is in
[`notifications.md`](notifications.md).

## Configuration

| Key | Default | Effect |
|---|---|---|
| `enabled` | `true` | Rejects creation and hides the page; moderation methods still work |
| `allowSeriesRequests` | `true` | Gates Series |
| `allowSeasonRequests` | `true` | Gates Season |
| `allowEpisodeRequests` | `true` | Gates Episode |
| `defaultQualityProfile` | `""` | Used at approval when no override or stored key exists |
| `maxOpenPerUser` | `0` | Maximum pending requests per user; `0` is unlimited |
| `blacklistOnDeny` | `false` | Also writes a blacklist row on denial |
| `autoApproveUsers` | `[]` | Case-insensitive username matches |
| `autoApproveRoles` | `[]` | Case-insensitive role matches |

There is no expiry, per-domain quota, approval-required switch or cleanup job.

## Sharp edges

1. `Approved` is not "available": nothing moves a request to `Available`.
2. Only provider ids match for duplicates, blacklists and removal.
3. Only one handler per capability; a duplicate is a startup error.
4. Only pending requests can be decided; failed approvals are terminal.
5. A title merely existing blocks season/episode requests.
6. Blacklisting is title- and domain-wide; there is no per-season scope or un-blacklist UI.
7. The page has no Failed tab, although the row remains.
8. Auto-approval can report success after a fulfilment failure; the row is `Failed` but the
   caller sees "approved automatically".
9. Quality labels can be misleading because Radarr and Sonarr profile ids are merged.
10. There is no transaction or concurrency token, so two admins can approve the same request.
11. Removal reconciliation covers TV/movies only.
