<p align="center">
  <img src="site/assets/logo.svg" width="84" alt="Arrk logo">
</p>

<h1 align="center">Arrk</h1>

<p align="center">
  <strong>Your entire media stack. One calm place.</strong>
</p>

<p align="center">
  One web UI and one config for a self-hosted media stack. Arrk runs and configures the
  services around your library — movies, shows, music, downloads and players — so you can stop
  visiting each one.
</p>

<p align="center">
  <a href="https://dynamiquel.github.io/Arrk/"><img alt="Website" src="https://img.shields.io/badge/website-visit-e879a0?style=flat-square"></a>
  <img alt="Status" src="https://img.shields.io/badge/status-alpha-fbbf24?style=flat-square">
  <img alt="License" src="https://img.shields.io/badge/license-MIT-c084fc?style=flat-square">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10-818cf8?style=flat-square">
  <img alt="Docker" src="https://img.shields.io/badge/Docker-ready-4ade80?style=flat-square">
</p>

<p align="center">
  <a href="https://dynamiquel.github.io/Arrk/setup.html">Get started</a> ·
  <a href="https://dynamiquel.github.io/Arrk/features.html">Features</a> ·
  <a href="https://dynamiquel.github.io/Arrk/guide.html">Setup guide</a> ·
  <a href="https://dynamiquel.github.io/Arrk/faq.html">FAQ</a>
</p>

---

Arrk is the front door to a self-hosted media setup. It provisions the services that do the
work, configures them through their APIs, and gives you a single place to browse, request, watch,
listen and clean up.

It does **not** replace those services. Radarr, Sonarr, Prowlarr, Transmission, Jellyfin,
Navidrome, slskd and friends keep doing what they are good at. Their own UIs remain reachable for
advanced work. Arrk simply makes the whole thing feel like one product.

> **A personal project, shared early.**
> Arrk started as my answer to the headaches of stitching together self-hosted media tools:
> too many tabs, too many logins, and configuration that drifted. It is still an early, actively
> changing project — features move, config evolves and rough edges exist. Treat it as a labour of
> love, not a commercial product. Back up your config, read the docs, and expect the occasional
> change.

## What it brings to the table

| What you get | Why it matters |
|---|---|
| **One front door** | A dashboard for discovery, requests, downloads, playback, storage and service health. One login for the household. |
| **Movies, shows and anime** | Trending and seasonal discovery, quality profiles, requests, local uploads and failed-download recovery — without hopping between apps. |
| **A real music library** | Deezer metadata, torrent and Soulseek acquisition, playlists, lyrics and Navidrome playback. Arrk owns the library itself. |
| **Playback without leaving** | An in-site Jellyfin player with audio and subtitle selection, Continue Watching and next-episode autoplay. |
| **Downloads that behave** | Transmission and slskd in one place, with seeding goals, storage limits and speed-aware slow/stalled cleanup. |
| **A library that ages well** | Per-user watch history, watchlists, stale-media warnings, pins and opt-in automatic cleanup. |
| **People and requests** | Invite-only accounts, roles, OIDC for players, Discord and MyAnimeList sign-in, and a moderated request flow. |
| **Fewer surprises** | Centralised notifications through Discord and webhooks, routed per topic, plus an in-app inbox and event feed. |
| **AI** | Ask about your library, get help with Arrk and choose which tasks AI helps with. Works with cloud or local models; the rest of Arrk works without it. |
| **Built to extend** | Every service is a module. Drop-in plugins use the same contracts for pages, navigation, jobs and dashboard tiles. |

See the [feature tour](https://dynamiquel.github.io/Arrk/features.html) for the full picture.

## Requirements

- A Linux host with Docker and access to `/var/run/docker.sock`.
- Three host directories for configuration, downloads and media.
- Outbound internet access for images, metadata and indexers. Inbound access is optional.

> Mounting the Docker socket lets Arrk create and remove containers. Run it on a host you
> control, and treat the Arrk container as root-equivalent.

## Quick start

Arrk is published as `ghcr.io/dynamiquel/arrk:latest`. The launcher creates the
storage directories and Docker network, pulls the published image and starts Arrk:

```bash
./scripts/run-arrk.sh
```

The UI is available at `http://localhost:5186`. Data defaults to `./arrk-data`; pass
`--data-root /srv/arrk` to use another location. The launcher reuses `./stackarr-data` when
it exists. Compose retains `./stackarr-data` as its default storage root to keep upgrades safe;
set `ARRK_CONFIG_DIR`, `ARRK_DOWNLOADS_DIR` and `ARRK_MEDIA_DIR` to choose different mounts.

The same deployment with Docker Compose:

```bash
docker compose up -d
```

Or pull and run the container directly:

```bash
docker pull ghcr.io/dynamiquel/arrk:latest

docker network create stackarr-media-net

docker run -d \
  --name arrk \
  --network stackarr-media-net \
  --restart unless-stopped \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v /srv/arrk/config:/config \
  -v /srv/arrk/downloads:/downloads \
  -v /srv/arrk/media:/media \
  -p 5186:5186 \
  -e StackName=media \
  ghcr.io/dynamiquel/arrk:latest
```

`StackName` namespaces every container Arrk creates (`stackarr-<name>-*`) and its Docker
network (`stackarr-<name>-net`), so several instances can share one host.

The first start creates `/config/arrk.yaml` with defaults and a placeholder administrator
(`admin` / `admin`). **Change the password after signing in.** Every other option has a default;
a minimal config only sets storage, VPN and routes.

### Moving an existing installation to Arrk

Back up your config directory, stop the old application, and keep mounting that **same directory**
at `/config` when switching images. **Do not rename or delete the database.** Arrk opens an existing
`stackarr.db` in place, including its SQLite WAL/shared-memory files, and reads and edits existing
`stackarr.yaml` / `stackarr.json` configuration files. These legacy files take precedence if both
old and new names exist. Fresh standalone installations use `arrk.db` and `arrk.yaml` instead.
The database tables and EF migration IDs are unchanged, so users, settings and library state are
retained. Configuration history remains available without renaming archived files.

`STACKARR_DATA_ROUTE` remains supported when `ARRK_DATA_ROUTE` is not set. Compose accepts the old
`STACKARR_CONFIG_DIR`, `STACKARR_DOWNLOADS_DIR`, `STACKARR_MEDIA_DIR` and `STACKARR_STACK_NAME`
variables as fallbacks for their `ARRK_*` equivalents. Never run both versions against the same
database. Old queued background jobs are not migrated; recurring jobs are registered by Arrk.

Internal Docker resource names and management labels retain the `stackarr` prefix so existing
containers, networks and volumes remain discoverable. Keep the same stack name and storage mounts.
Existing authentication keys and encrypted database tokens retain their original protection identity.
Rebuild drop-in plugins against the `Arrk.*` assemblies before loading them.

New to this? The [setup page](https://dynamiquel.github.io/Arrk/setup.html) covers
requirements, storage layout and troubleshooting. The [setup guide](https://dynamiquel.github.io/Arrk/guide.html)
walks through metadata, quality profiles, indexers, VPN, players, subtitles, remote access, users
and notifications in a sensible order.

## How it fits together

Arrk is one modular ASP.NET Core application, not a microservice per feature. It creates and
configures sibling Docker containers, then talks to each service over its API.

| Your concept | What Arrk runs |
|---|---|
| Movies & shows | Radarr + Sonarr, with TMDb discovery |
| Music | Arrk's native library + Deezer metadata + slskd + Navidrome |
| Downloads | Transmission and Soulseek (slskd) |
| Indexers | Prowlarr |
| Video playback | Jellyfin, behind Arrk's in-site player |
| Remote access | Traefik, with an optional Cloudflare tunnel |
| VPN | Gluetun, opt-in per service |

There is no extra database container: Arrk, Hangfire and the managed services all use SQLite.
The [architecture page](https://dynamiquel.github.io/Arrk/how-it-works.html) goes deeper, and
[`docs/features/`](docs/features/) holds the per-feature design notes.

## Project status

Arrk is pre-1.0 and moving quickly. The core everyday loop — discover, request, download,
watch/listen, clean up — is real and used daily. Still in progress: automatic TLS certificates, a
dedicated simplified non-admin landing page, notifications for every lifecycle event, more AI
actions, and cleaner boundaries for a few service clients.

If you need a frozen platform with a support contract, this is not it yet. If you enjoy shaping a
stack with the person building it, welcome.

## Documentation

- [Website](https://dynamiquel.github.io/Arrk/) — overview, features, setup and FAQ.
- [`docs/features/`](docs/features/) — per-feature design notes.
- [`docs/features/modules.md`](docs/features/modules.md) — writing a plugin.
- [`AGENTS.md`](AGENTS.md) — the repository contract for coding agents, including build and
  pull-request conventions.

## Development

Requires the .NET 10 SDK. Docker is optional: the default test run skips Docker-backed suites
when no daemon is reachable.

Bootstrap once:

```bash
dotnet tool restore
dotnet restore Arrk.slnx

./scripts/ci.sh              # restore, build and run hermetic tests
./scripts/ci.sh --docker     # also run the Docker-backed suites
```

The solution is split into one module per concern under `src/Arrk.*` and one test project per
module under `tests/Arrk.*.Tests`. Tests use TUnit on Microsoft.Testing.Platform.

Work on a branch and open a pull request. Releases are managed in CI; do not bump the version
in a pull request.

The public website lives in [`site/`](site/) and deploys to GitHub Pages.

## Contributing

Issues and pull requests are welcome. Please keep reports clear and remove API keys, passwords and
personal paths before posting. There is no support contract or guaranteed response time — this is
a personal project that happens to be shared.

## License

MIT. See [`LICENSE`](LICENSE).
