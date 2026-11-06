# MyAnimeList (`Arrk.Mal`)

The MyAnimeList integration. MAL is a specific provider, not a generic anime abstraction.

`Arrk.Mal` links an Arrk user's MAL account over OAuth, shows and edits their anime list in
the app, and pushes completed watch activity to MAL. The same identity can sign in to Arrk.

```
/account or /login
      |  /account/link/mal or /login/mal
      v
/mal-signin/start -> myanimelist.net authorize -> /mal-signin?code&state
      |
      +-> AspNetUserLogins (sign-in identity)
      +-> MalAccount (encrypted tokens)
                |
                v
            /mal  -> MalListService -> api.myanimelist.net/v2
                |
                +-> MAL Plan to Watch = virtual IExternalWatchlist
                +-> MAL row in the TV series dialog
```

## Configuration

```yaml
services:
  mal:
    enabled: true
    clientId: ""            # https://myanimelist.net/apiconfig
    clientSecret: ""
    redirectUris: []        # optional; defaults to the origin the user opened
    allowLogin: true
    syncMinutes: 10
    apiBaseUrl: https://api.myanimelist.net/v2
    oauthBaseUrl: https://myanimelist.net/v1/oauth2
    datasetUrl: https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-mini.json
    datasetRefreshDays: 7
```

Register the exact redirect URI (default `{origin}/mal-signin`) in the MAL API app. The flow uses
the first configured URI matching the origin that started it; otherwise the current origin.

## OAuth and tokens

- Authorization code with `plain` PKCE, the only method MAL supports.
- The state is a Data Protection payload containing the verifier, mode (`login` or `link`), user
  id for links, return URL and exact redirect URI. No state cookie is needed.
- Access and refresh tokens are encrypted before storage and never sent to the browser or logged.
  Access tokens refresh shortly before expiry; a failed refresh marks the account `NeedsReauth`
  and the UI offers Reconnect.
- Linking also adds an Identity external login (`mal`), so MAL can sign in. Unlinking follows the
  same "do not remove the only login method" rule as Discord.
- MAL's `picture_url` becomes the Arrk avatar when the account has none; a failed download
  never blocks sign-in. MAL never receives an Arrk password.

## List and metadata

MAL is the source of truth for status, score, progress, tags and comments. Arrk caches
`MalListEntry` and `MalCatalogEntry` so pages and the watchlist shelf render instantly.

- TMDB is preferred for artwork, title, overview, year and genres. The anime-lists dataset maps
  MAL ids to TMDB TV/movie ids and seasons; MAL's own picture/metadata is the fallback.
- Only the visible slice is enriched with bounded concurrency, so a 500-entry list does not hammer
  TMDB.
- Score is MAL's 0–10 integer; editing it patches MAL.
- `/mal` has My List and Browse/Search sharing one search bar, with status filters as tabs. The
  detail dialog edits status, score and progress and links to MAL.
- Related entries (sequels, prequels, side stories, spin-offs) load in place, so cours can be
  traversed without leaving the popup.
- Cards use the shared poster card; the MAL title appears in the hover overlay. The MAL link
  appears beside TMDB/IMDb in library and discovery popups.

## Mapping local seasons to MAL

MAL models cours and seasons as separate entries; Sonarr/Jellyfin model a series with seasons.
The [Fribb/anime-lists](https://github.com/Fribb/anime-lists) dataset (cached under
`<config>/mal/anime-lists.json`, refreshed weekly) translates:

| Shape | Example | Mapping |
|---|---|---|
| One local season to one MAL entry | One Punch Man S1 | One part |
| One local season to several MAL entries | Attack on Titan S3 | Parts by episode range using `episode_offset` |
| One MAL entry to a whole local series | One Piece | Per-season part; MAL start follows previous counts |
| Movie | A Silent Voice | TMDB movie id to MAL id |

`MalMap`/`MalMapPart` store `LocalEpisodeStart`, `LocalEpisodeEnd` and `MalEpisodeStart`; the
translation is `malEpisode = MalEpisodeStart + (localEpisode - LocalEpisodeStart)`. TVDB is used
only as the local coordinate; no TVDB API is called. Mappings are auto-created; unmatched titles
are skipped and logged.

## Watch-progress sync

`MalWatchSyncJob` runs every `syncMinutes` and pushes completed activity for linked, sync-enabled
users:

- Only completed plays count (the recorder marks 90%+ as completed).
- Group by series/season, take the highest watched episode, translate and patch
  `num_watched_episodes`.
- `plan_to_watch` becomes `watching` on first progress; `watching` becomes `completed` at MAL's
  known episode total.
- Progress never decreases; `on_hold`, `dropped` and `completed` are never overridden.
- Movies become `completed` with one episode. MAL never marks local/Jellyfin episodes watched.

## Plan to Watch watchlist

MAL `plan_to_watch` entries appear as a virtual watchlist through `IExternalWatchlist`:

- No rows are copied; the MAL cache is the source of truth.
- `WatchlistSections` adds MAL Plan to Watch and Watching tabs on `/tv` and (for films)
  `/movies`, with a capped shelf and a View all link.
- `WatchlistButton` adds/removes the mapped entries. Adding puts not-yet-tracked entries on Plan
  to Watch and leaves watching/completed alone; removing touches only Plan to Watch.
- Changes on MAL appear after the next pull; changes in Arrk patch or delete on MAL
  immediately. Progress itself remains one-way.

## TV dialog row

`MalSeriesStatusRow` contributes to Tvarr's `tv.series.season` region: status, progress and score
per mapped MAL entry, one chip per split part. Clicking a chip opens `MalDetailDialog`. It is
hidden when MAL is not linked or the season has no mapping.

## Invariants

- One MAL account per Arrk user, and one Arrk user per MAL account (both unique).
- Tokens are encrypted at rest and used only server-side.
- MAL score is 0–10; 0 means not rated.
- Progress is monotonic from Arrk's side; a lower edit made on MAL is left alone.
- MAL entries stay per anime entry; Arrk groups them for display but never merges progress.
- A failed dataset refresh keeps the last good copy.

## Limitations

- No manga support.
- No manual mapping-correction UI.
- No MAL → local watched-state sync.
- No per-episode rating; MAL has one 0–10 score per entry.
- MAL publishes no exact rate limits; requests are serialised about 650 ms apart and retried.
- An unused refresh token expires after roughly a month, requiring reconnect.
