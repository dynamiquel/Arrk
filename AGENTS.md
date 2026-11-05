# AGENTS.md: working on Stackarr

This is the repository contract for coding agents (Codex, OpenCode, Claude Code, Copilot,
etc.). It assumes a fresh clone and no machine-specific state. Follow it unless the task
explicitly says otherwise.

## What Stackarr is

Stackarr is one modular ASP.NET Core 10 application that provisions and configures a
self-hosted media stack. Read `README.md` first, then the matching note under
`docs/features/` for the area you are changing.

- .NET 10 (`net10.0`), ASP.NET Core + Blazor/MudBlazor, SQLite (EF Core), Docker.DotNet,
  Hangfire, Serilog.
- One module per concern under `src/Stackarr.*` (`IStackarrModule`); one test project per
  module under `tests/Stackarr.*.Tests`, plus `Stackarr.IntegrationTests` and
  `Stackarr.TestSupport`.
- Tests are TUnit on `Microsoft.Testing.Platform` (`global.json`).
- The public website lives in `site/` and deploys through GitHub Pages.

## Get a working environment

You need:

1. The .NET 10 SDK selected by `global.json`.
2. Git and, for pull requests, an authenticated `gh` or a git remote with push access.
3. Docker **optional**; the default test run skips Docker-backed tests when no daemon is
   reachable.

Bootstrap once:

```bash
dotnet tool restore
dotnet restore Stackarr.slnx
```

No secrets are required for the default build or test run. Live tests read environment
variables and return early when they are unset.

## Git workflow

You are one agent among many. **Never push directly to `main`.** Work on a branch, commit
there, and open a pull request. A human decides when to merge.

### Remote or ephemeral checkout (one agent per clone)

```bash
git switch -c agent/<task-slug>
# ...edit, build, test...
git add -A
git commit -m "<focused message>"
git push -u origin agent/<task-slug>
gh pr create --fill --base main
```

### Shared checkout (several agents on one host)

Do not edit the shared working tree. Create an isolated worktree and do everything there:

```bash
git worktree add .worktrees/<task-slug> -b agent/<task-slug>
cd .worktrees/<task-slug>
# ...edit, build, test, commit...
```

Then push the branch and open a pull request as above. Only the maintainer merges. Never
touch another agent's worktree, branch, containers or network.

### Rules

- Keep commits focused and the message descriptive.
- Do not rewrite shared history. Force-push only your own branch, preferably with
  `--force-with-lease`.
- Never commit secrets, `.env` files, API keys, generated config, `config/`, `data/`,
  `test-data/`, `stackarr-data/` or machine-specific paths.
- If the checkout has no git remote, stop and ask: you cannot open a pull request without
  one.
- Do not tag releases or publish packages from an agent task. Release workflows own that.

## Build and test

The canonical entry point is:

```bash
./scripts/ci.sh              # restore + build + hermetic tests
./scripts/ci.sh --docker     # same, but also run Docker-backed tests
```

Manual equivalent:

```bash
dotnet build Stackarr.slnx
dotnet test --solution Stackarr.slnx
```

Docker-backed suites skip themselves when the daemon is unreachable. To force a hermetic
run, point `DOCKER_SOCKET` at a path that does not exist:

```bash
DOCKER_SOCKET=/var/run/docker.sock.unavailable ./scripts/ci.sh
STACKARR_SKIP_DOCKER_TESTS=1 ./scripts/ci.sh   # same effect, keeps the default socket
```

### Docker test rules

- Set a unique `STACKARR_TEST_RUN_ID` when you run container tests. Tests share the
  `stackarr-tests-net` network and scope every container, volume and network by run id.
  `./scripts/ci.sh --docker` serializes test modules so parallel projects cannot exhaust
  Docker's default address pool.
- Never delete containers, volumes or networks you did not create.
- Do not run Docker-backed tests against a daemon that another agent is actively using
  unless you can isolate your run. The suite is safe to share, but cleanup must stay
  scoped.
- Some test fixtures bind-mount their data directories from the test output folder. Keep
  test data under temp directories and never assume the build output is writable by the
  container.

### Live and network tests

Live tests are opt-in and must stay env-gated (`STACKARR_SKIP_LIVE_TESTS=1` forces them
off). They are never required for a pull request. Follow the existing patterns:

- `STACKARR_LIVE_BASE_URL` for a running Stackarr instance.
- `STACKARR_LIVE_*` for service-specific live checks.
- `STACKARR_SKIP_LIVE_TESTS=1` in CI and hermetic runs.

## What to change

- **Tests for every new feature.** Prefer real APIs and real Docker containers over fake
  clients. Only fake the network where a real call is impossible
  (`FakeHttpMessageHandler`).
- **Explicit types.** No `var`; use explicit types and target-typed `new()` where the type
  is obvious. Convert `var` in code you touch.
- **Modern C#** where it reads better: collection expressions, pattern matching, primary
  constructors, `required`, file-scoped namespaces.
- **Module boundaries.** Cross-module and plugin seams use the abstraction assemblies.
  Concrete types stay concrete and are registered directly in DI; do not add an interface
  where a concrete type suffices.
- **UI consistency.** Reuse `Components/Shared/*` primitives and the existing theme.
  Componentise new UI; do not build one-off markup.
- **Definitions and wrappers stay separate.** `IServiceDefinition` describes a service;
  `Service` wraps it.

## Documentation

- `README.md` is the user-facing overview and quick start.
- `docs/features/<area>.md` holds the lasting design for a feature: model, invariants,
  config, file map and tests.
- `site/` is the public website.
- Update the relevant note when behaviour or configuration changes. Do not add changelog
  or status-narration sections; commit messages and tests carry history.
- Keep relative links valid.

## Versioning and releases

**Do not edit the version yourself.** `Directory.Build.props` is only the development
fallback. The release workflow validates the requested version, updates
`Directory.Build.props`, tags the commit, builds the .NET assemblies with that version and
publishes the container image.

Agents must not bump `<Version>`, create tags or push release images.

## Definition of done

- `./scripts/ci.sh` passes.
- `./scripts/ci.sh --docker` passes when Docker is available (or note in the pull request
  that it was skipped).
- New behaviour has tests, and the matching `docs/features/` note is updated.
- The branch is pushed and a pull request is open. Do not merge it yourself.
