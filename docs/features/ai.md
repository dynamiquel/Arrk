# AI

Arrk has an optional AI layer. It is off by default, provider-agnostic, and extends
deterministic features rather than replacing them.

## Principles

1. **Opt-in per feature.** `ai.enabled` is the master switch for model calls. Each feature can
   be turned off individually. The assistant is rate-limited separately. MCP is an independent
   opt-in tool server and needs no configured model.
2. **Advisory for facts, authoritative through services.** A model may rank, propose, explain or
   choose an action. Library state, availability and verification always come from code. Actions
   run through the same service the UI uses, with the same permissions and audit trail, after
   explicit confirmation. Every path falls back to non-AI behaviour.
3. **State comes from tools; opinions may come from the model.** Library, download, availability
   and action facts must come from a tool. Subjective judgements and general knowledge are
   framed as opinion. Untrusted input such as release names and titles is data, never
   instructions.
4. **Cheap first, cached and bounded.** Small candidate sets, Low for ranking, Medium for
   everyday assistance and High for demanding questions, token/request budgets and a SQLite
   decision-response cache. Live library facts and writes are not cached across turns.
5. **Automatic and on-demand features are separate.** Automatic features are explicit opt-ins
   because they spend tokens without the user asking.
6. **No AI in the event pipeline.** Failures and lifecycle events never call a model. Diagnosis
   is a chat tool the user invokes.

## Architecture

`src/Arrk.Ai` is a module and Razor class library. It references no domain module. Domain
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
    low: gemini-3.5-flash-lite      # falls back to Medium when blank
    medium: gemini-3.6-flash
    high: ""                       # falls back to Medium when blank
  features:
    assistant:
      enabled: true
      maxMessagesPerHour: 30
      maxTokensPerUserPerDay: 60000
      maxToolRounds: 2
      maxOutputTokens: 1200         # per provider response, 128–8192
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
  mcp:
    enabled: false
    clients: []                     # dedicated credentials, bound to existing users
```

Any OpenAI-compatible host works; `baseUrl` overrides the provider preset. Module-specific AI
toggles live in that module's section and appear on the admin page through
`IAiFeatureDescriptor`.

`models.chat` remains a legacy fallback when `models.medium` is blank; `models.fast` likewise
supplies Low when `models.low` is blank. Existing numeric usage roles remain compatible
(Medium=0, Low=1, High=2). The assistant's tier choice lasts for the
popup session; background features still explicitly use Low. Matching resolved model ids reuse
the same client instead of retrying a rate limit against an identical model.

## Features

| Feature | Owned by | Config |
|---|---|---|
| Assistant popup with actions | `Arrk.Ai` + module sources | `ai.features.assistant` |
| Release ranking (music first) | `Arrk.Music` | `ai.features.releaseRanking` |
| Local import matching | `Arrk.Tvarr` | `ai.features.importMatching` |
| Assistant recommendations | Tvarr and Music sources | `ai.features.assistant` |
| Personas | built-ins plus per-user choice | `ai.personas` |
| Download progress and failures | `Arrk.DownloadManagement` | `ai.features.assistant` |
| Host, storage, jobs, images | `Arrk.Orchestration` (admin) | `ai.features.assistant` |
| Request moderation | `Arrk.Requests` (admin) | `ai.features.assistant` |
| MyAnimeList updates | `Arrk.Mal` | `ai.features.assistant` |
| Custom CSS generation | `Arrk.Ai` | `ai.features.style` |
| Usage admin page | `Arrk.Ai` | admin only |
| Documentation tools | AI adapter over `Arrk.Core` module documentation | no provider required for retrieval |
| MCP tool server | `Arrk.Ai` | `ai.mcp` (independent of `ai.enabled`) |

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
six messages and eight recent item ids), so follow-ups can refer to earlier results. Closing
the popup discards the thread.

- Facts come from tools; the model may apply its own knowledge and opinions to those facts.
- Tools execute in code and return plain data. Two tool rounds are the default, configurable
  between one and four, with at most eight calls executed per round.
- Keyword routing offers likely packs without a routing model call. `discover_tools` and
  `invoke_tool` reach authorized tools missed by keywords or the 20-schema cap, including
  terse follow-ups. Discovery does not bypass module availability, permissions or confirmation.
- Model responses default to a 1,200-token ceiling. Conversation history has a 3,000-character
  ceiling. Tool bodies share a 6,000-character replay budget, newest first; an oversized body
  is explicitly omitted, never sliced into misleading partial JSON. Later action outcomes
  therefore cannot disappear behind a large earlier lookup. Small omission/name overhead is extra.
- A `429` retries once on a distinct Low client.
- `search_media` supports genre, person, year, anime, library/discovery scope and sort, so
  questions like "what anime do I have" or "that new Tom Cruise film" are one tool call.
- Search and recommendation wire results omit artwork URLs. `get_media` bounds overview
  text to 1,500 characters and facts to 20; related items are paged (default eight, maximum
  ten) with `relatedCount` and `nextRelatedOffset`. Fetch another page only when needed.
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
| Documentation | `search_documentation`, `read_documentation` |
| Runtime discovery | `discover_tools`, `invoke_tool` |
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

## Documentation retrieval

Documentation belongs to modules/plugins and is aggregated by `Arrk.Core.Documentation`, not
owned by AI. See [documentation.md](documentation.md) for contribution and retrieval contracts.
The same Markdown powers Help, the built-in website and AI retrieval; installed plugin content
does not need to be published on the website.

`DocumentationAssistantTool` supplies two always-discoverable schemas. Empty
`search_documentation` browses eight topics at a time with a continuation offset; keyword
queries return up to eight short section excerpts. `read_documentation` returns a bounded
section and continuation offset. The overview includes the first twelve section ids; search
can find all others. The model should extract specific terms and rephrase weak searches.
No whole corpus is injected into prompts, and no embedding service is required. Documentation
is reference data, never authorization, live state or instructions overriding the system prompt.

## MCP

The official C# MCP SDK hosts stateless Streamable HTTP at `/mcp`. It exposes the same
module-contributed functions without keyword routing or the chat schema cap and never calls a
model. Enable independently with `ai.mcp.enabled`. Configure clients, for example:

```yaml
ai:
  mcp:
    enabled: true
    clients:
      - name: desktop-assistant
        userId: 7                 # an existing approved account
        token: "<unique random token of at least 32 characters>"
        allowWrites: false
```

Connect a Streamable HTTP client to the public HTTPS `/mcp` URL and configure the header
`Authorization: Bearer <token>`. This is explicit header-based authentication, not OAuth
discovery/dynamic registration. Do not reuse provider API keys or put tokens in query strings.
Use a separate non-admin account and a separate credential per client. Replacing/removing a
credential revokes it on the next request. Keep normal reverse-proxy access/rate controls.

- Every HTTP request rechecks the account and roles through `IUserSessionReader`. Disabled,
  deleted and pending accounts are denied; promotions/demotions apply immediately.
- No browser cookie authentication; cross-origin `Origin` headers are rejected. Forward the
  correct public scheme/host through the normal trusted proxy configuration.
- Clients are read-only by default, using an explicit allowlist of known read functions.
  Newly contributed tools do not become read-only by naming convention.
- `allowWrites: true` offers non-destructive service-backed writes. Module-level user/admin
  checks still apply both when listing and calling tools.
- Delete and quality-changing tools are never exposed, including to write-enabled admins:
  the external model cannot press Arrk's trusted confirmation button. Use the Arrk UI instead.
- `resources/list` pages the installed documentation's section metadata, 32 entries at a time.
  `resources/read` accepts only exact registered `arrk://documentation/<module>/<topic>/<section>`
  URIs and returns that section's original Markdown. Clients decide whether to attach these
  resources to context; search/read tools remain available too. These handlers share the
  same bearer/account checks, need no model and never fetch arbitrary URLs or files.
- No arbitrary external MCP connections, shell/file execution or raw configuration/secrets tool.

## Admin usage page

`/ai` is admin-only and shows provider/model status with a Test AI action; daily, weekly and
monthly calls and tokens split automatic/chat; per-feature and per-user usage, failures and
latency; release-ranking shadow agreement; cache size and purge; budget consumption; recent
calls when `privacy.storePromptText` is enabled; and retention actions.

## Non-goals

- AI-driven index search; Prowlarr matching is fine.
- AI in notifications or the event log.
- Automatic AI grabbing of TV/movie releases. The arrs own that.
- Connecting the in-app assistant to arbitrary third-party MCP servers or bots.

## Adding a provider

An OpenAI-compatible host needs no code change: use `custom` with its `baseUrl`. To make it a
preset, add the slug and default endpoint to `AiProviders`. A different protocol goes behind the
same `IChatClient` seam.
