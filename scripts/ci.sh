#!/usr/bin/env bash
#
# ci.sh — one command for agents and CI.
#
#   ./scripts/ci.sh              restore, build and run hermetic tests
#   ./scripts/ci.sh --docker     also run Docker-backed tests
#   ./scripts/ci.sh --no-tests   restore and build only
#
# The hermetic run points DOCKER_SOCKET at a path that does not exist so Docker-backed
# suites skip themselves. That is what pull-request CI does.
#
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname -- "$SCRIPT_DIR")"
cd -- "$REPO_ROOT"

CONFIGURATION="Release"
RUN_TESTS=1
RUN_DOCKER=0

usage() {
    cat <<'EOF'
ci.sh — restore, build and test Stackarr.

Usage:
  ./scripts/ci.sh [options]

Options:
  --docker            Run Docker-backed tests too (requires a reachable daemon).
  --no-tests          Restore and build only.
  -c, --configuration Use Debug or Release (default: Release).
  -h, --help          Show this help.

Environment:
  DOCKER_SOCKET          Docker socket path (default: /var/run/docker.sock).
  STACKARR_TEST_RUN_ID   Pin the Docker run id; otherwise one is generated.
  STACKARR_SKIP_LIVE_TESTS
                         Defaults to 1 here; live tests never run in CI.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --docker)          RUN_DOCKER=1; shift ;;
        --no-tests)        RUN_TESTS=0; shift ;;
        -c|--configuration) CONFIGURATION="$2"; shift 2 ;;
        -h|--help)         usage; exit 0 ;;
        *)                 echo "error: unknown option: $1 (try --help)" >&2; exit 1 ;;
    esac
done

export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export STACKARR_SKIP_LIVE_TESTS="${STACKARR_SKIP_LIVE_TESTS:-1}"

if [ "$RUN_DOCKER" -eq 1 ]; then
    export DOCKER_SOCKET="${DOCKER_SOCKET:-/var/run/docker.sock}"
    export STACKARR_TEST_RUN_ID="${STACKARR_TEST_RUN_ID:-ci-$(date +%s)-$$}"
    echo "==> Docker tests enabled (socket: $DOCKER_SOCKET, run: $STACKARR_TEST_RUN_ID)"
else
    # A path that cannot exist makes every Docker probe fail fast, so those suites skip.
    export DOCKER_SOCKET="${DOCKER_SOCKET_UNAVAILABLE:-/var/run/docker.sock.unavailable}"
    echo "==> Hermetic run (Docker tests skipped)"
fi

echo "==> Restoring tools"
dotnet tool restore

echo "==> Restoring solution"
dotnet restore Stackarr.slnx

echo "==> Building ($CONFIGURATION)"
dotnet build Stackarr.slnx --no-restore -c "$CONFIGURATION"

if [ "$RUN_TESTS" -eq 1 ]; then
    echo "==> Testing ($CONFIGURATION)"
    TEST_ARGS=(--solution Stackarr.slnx --no-build -c "$CONFIGURATION")
    if [ "$RUN_DOCKER" -eq 1 ]; then
        # Docker tests create networks in several projects. Running modules in parallel can
        # exhaust Docker's small default address pool, so serialize them for this run.
        TEST_ARGS+=(--max-parallel-test-modules 1)
    fi
    dotnet test "${TEST_ARGS[@]}"
fi

echo "==> Done"
