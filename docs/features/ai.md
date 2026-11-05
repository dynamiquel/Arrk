# AI

Stackarr has an optional AI layer. It is off by default, provider-agnostic, and extends
deterministic features rather than replacing them.

## Principles

1. **Opt-in per feature.** `ai.enabled` is the master switch. Each feature can then be turned off
   individually. Chat is rate-limited separately.
2. **Advisory for facts, authoritative through services.** A model may rank, propose, explain or
   choose an action. Library state, availability and verification always come from code. Actions
   run through the same service the UI uses, with the same permissions and audit trail, after
   explicit confirmation. Every path falls back to non-AI behaviour.
3. **State comes from tools; opinions may come from the model.** Library, download, availability
   and action facts must come from a tool. Subjective judgements and general knowledge are
   framed as opinion. Untrusted input such as release names and titles is data, never
   instructions.
4. **Cheap first, cached and bounded.** Small candidate sets, a `fast` model for ranking and a
   `chat` model for conversation, token/request budgets and a SQLite response cache.
5. **Automatic and on-demand features are separate.** Automatic features are explicit opt-ins
   because they spend tokens without the user asking.
6. **No AI in the event pipeline.** Failures and lifecycle events never call a model. Diagnosis
   is a chat tool the user invokes.

## Architecture

`src/Stackarr.Ai` is a module and Razor class library. It references no domain module. Domain
modules implement narrow contracts and register them in their own `ConfigureServices`; the AI
module discovers them through DI.

| Contract | Implemented by | Purpose |
|---|---|---|
| `IAssistantTool` | any module | A tool pack (functions, access level, availability) |
| `IAssistantMediaSource` | Tvarr, Music | Structured search and detail across domains |
| `IAssistantMediaActionSource` | Tvarr, Music | Request, watchlist, delete, upgrade and play |
| `IAssistantRecommendationSource` | Tvarr, Music | Deterministic candidates with factual reasons |
| `IAiFeatureDescriptor` | any module | Admin usage page listing and measurement |

AI-owned entities (`AiUsageRecord`, `AiCacheEntry`, `AiDecisionOutcome`) use the standard module
entity configuration and migration flow.

## Configuration

```yaml
ai:
  enabled: true
  provider: gemini
  apiKey: ""
  baseUrl: ""
  models:
    chat: gemini-3.6-flash
    fast: gemini-3.5-flash-lite     # falls back to chat when blank
  features:
    assistant:
      enabled: true
      maxMessagesPerHour: 30
      maxTokensPerUserPerDay: 60000
      maxToolRounds: 2
    releaseRanking:
      enabled: true
      mode: shadow                  # off | shadow | ambiguous | always
      maxCandidates: 12
      minConfidence: 0.6
      ambiguityScoreWindow: 5
    importMatching:
      enabled: true
      mode: ambiguous               # off | shadow | ambiguous | always
      maxCandidates: 8
      minConfidence: 0.6
      ambiguityScoreWindow: 8
    recommendations:
      enabled: false
    digest:
      enabled: false
      schedule: "0 8 * * *"
    style:
      enabled: true                  # per-account CSS from a brief
  limits:
    maxRequestsPerDay: 500
    maxTokensPerMonth: 2000000
    cacheMinutes: 60
  personas:
    default: plain
    custom:
      - key: johnson
        name: Sergeant Johnson
        description: Blunt, confident Marine sergeant.
        style: Short, dry military cadence; keep it respectful.
  privacy:
    storePromptText: false
```

Any OpenAI-compatible host works; `baseUrl` overrides the provider preset. Module-specific AI
toggles live in that module's section and appear on the admin page through
`IAiFeatureDescriptor`.

## Features

| Feature | Owned by | Config |
|---|---|---|
| Assistant popup with actions | `Stackarr.Ai` + module sources | `ai.features.assistant` |
| Release ranking (music first) | `Stackarr.Music` | `ai.features.releaseRanking` |
| Local import matching | `Stackarr.Tvarr` | `ai.features.importMatching` |
| Assistant recommendations | Tvarr and Music sources | `ai.features.assistant` |
| Personas | built-ins plus per-user choice | `ai.personas` |
| Download progress and failures | `Stackarr.DownloadManagement` | `ai.features.assistant` |
| Host, storage, jobs, images | `Stackarr.Orchestration` (admin) | `ai.features.assistant` |
| Request moderation | `Stackarr.Requests` (admin) | `ai.features.assistant` |
| MyAnimeList updates | `Stackarr.Mal` | `ai.features.assistant` |
| Custom CSS generation | `Stackarr.Ai` | `ai.features.style` |
| Usage admin page | `Stackarr.Ai` | admin only |

Music query expansion, import rescue, per-user "For you" recommendations and the digest are
planned.

### Release ranking

Release ranking covers album torrents and individual Soulseek files. It is additive: it never
rewrites deterministic scoring in `MusicAcquisitionService`.

- Runs after filtering, over the eligible top `maxCandidates`.
- `shadow` records the pick and changes nothing. `ambiguous` acts only on ties or edition
  markers. `always` picks among the top N. `off` stays deterministic.
- The model gets bounded facts: expected album and candidate titles, indexer, size, seeders,
  score, lossless flag and edition markers.
- It returns `{ pick, confidence, reason }`. Unknown ids are discarded, low confidence abstains,
  and any failure leaves the deterministic choice untouched.
- Import verification remains the final gate. Outcomes are recorded for comparison on `/ai`.

### Assistant

The assistant is an app-bar overlay, not a page. It keeps a short in-memory thread (at most
eight turns), so follow-ups can refer to earlier results. Closing the popup discards the thread.

- Facts come from tools; the model may apply its own knowledge and opinions to those facts.
- Tools execute in code and return plain data. At most three tool rounds are allowed.
- Pack routing is keyword-based and costs no extra model call. Tool schemas, tool output,
  conversation length and search results are all capped.
- A `429` from the chat model retries once on the `fast` model.
- `search_media` supports genre, person, year, anime, library/discovery scope and sort, so
  questions like "what anime do I have" or "that new Tom Cruise film" are one tool call.
- Domain actions call the same services as the UI, so quotas, notifications, events and
  permissions do not fork. Delete and quality changes are administrator-only.
- Destructive and quality-changing tools return a one-click confirmation. The button runs through
  `AssistantActionRunner` without a model call, so a model cannot delete or re-download on its
  own.
- Provider errors become a friendly status-aware message; the exception stays in the log.

Personas change wording only. Plain, Jarvis and Cortana are built in; Johnson, Emilia and Coconut
ship as editable custom entries. The choice is stored per user.

### Packs

| Pack | Tools |
|---|---|
| Meta | `get_capabilities`, `get_help` |
| Library | `search_media`, `get_media`, `get_continue_watching` |
| TV recommendations | `tv_recommend_media` |
| Music recommendations | `music_recommend_media` |
| TV actions | `tv_request_media`, `tv_add_to_watchlist`, `tv_remove_from_watchlist`, `tv_delete_media`, `tv_upgrade_quality`, `tv_play_media` |
| Music actions | `music_request_media`, `music_add_to_watchlist`, `music_remove_from_watchlist`, `music_delete_media`, `music_play_media` |
| Downloads | `downloads_progress`, `downloads_failures`, `downloads_pause`, `downloads_resume` |
| Ops (admin) | `ops_host_status`, `ops_storage`, `ops_jobs`, `ops_image_updates`, `ops_recent_events` |
| Requests | `requests_list`, `requests_summary`, `requests_approve`, `requests_deny`, `requests_approve_all`, `requests_revoke` |
| MyAnimeList | `mal_list`, `mal_update` |

Domain packs are registered by their own module and bound to a runner containing only that
module's action source, so a `tv_*` tool cannot reach music or a plugin. Media ids carry a domain
prefix (`tv:...`, `music:...`), so detail drill-down works without the runtime knowing a domain.

## Admin usage page

`/ai` is admin-only and shows provider/model status with a Test AI action; daily, weekly and
monthly calls and tokens split automatic/chat; per-feature and per-user usage, failures and
latency; release-ranking shadow agreement; cache size and purge; budget consumption; recent
calls when `privacy.storePromptText` is enabled; and retention actions.

## Non-goals

- AI-driven index search; Prowlarr matching is fine.
- AI in notifications or the event log.
- Automatic AI grabbing of TV/movie releases. The arrs own that.
- External MCP servers or bots.

## Adding a provider

An OpenAI-compatible host needs no code change: use `custom` with its `baseUrl`. To make it a
preset, add the slug and default endpoint to `AiProviders`. A different protocol goes behind the
same `IChatClient` seam.
