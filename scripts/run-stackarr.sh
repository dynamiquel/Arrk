#!/usr/bin/env bash
#
# run-stackarr.sh — start a Stackarr instance with one command.
#
# Stackarr provisions and manages the whole media stack (Radarr, Sonarr, Prowlarr,
# Jellyfin, the reverse proxy, the VPN sidecar, downloads, ...) itself. This script only
# does the boring host-side bits: create the storage directories, create the Docker
# network, and run the Stackarr container with the right mounts and StackName.
#
# Examples:
#   # Default: ./stackarr-data/{config,downloads,media}, UI on http://localhost:5186
#   ./scripts/run-stackarr.sh
#
#   # Build the image first, then run a tunnel-only instance (no host ports at all)
#   ./scripts/run-stackarr.sh --build --data-root /srv/stackarr \
#       --name stackarr-media --stack-name media --no-port
#
#   # Point at existing storage and publish the UI on a different port
#   ./scripts/run-stackarr.sh \
#       --config-dir /srv/stackarr/config \
#       --downloads-dir /srv/downloads \
#       --media-dir /srv/media \
#       --host-port 8080
#
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname -- "$SCRIPT_DIR")"

CONTAINER_NAME="stackarr"
STACK_NAME=""
IMAGE="stackarr:latest"
DATA_ROOT="./stackarr-data"
CONFIG_DIR=""
DOWNLOADS_DIR=""
MEDIA_DIR=""
NETWORK=""
DOCKER_SOCKET="/var/run/docker.sock"
HOST_PORT="5186"
PUBLISH_PORT=1
RESTART_POLICY="unless-stopped"
BUILD=0
FORCE=0
EXTRA_ENV=()

usage() {
    cat <<'EOF'
run-stackarr.sh — start a Stackarr instance with one command.

Stackarr provisions and manages the whole media stack (Radarr, Sonarr, Prowlarr,
Jellyfin, the reverse proxy, the VPN sidecar, downloads, ...) itself. This script only
does the host-side bits: create the storage directories, create the Docker network, and
run the Stackarr container with the right mounts and StackName.

Examples:
  # Default: ./stackarr-data/{config,downloads,media}, UI on http://localhost:5186
  ./scripts/run-stackarr.sh

  # Build the image, then run a tunnel-only instance (no host ports at all)
  ./scripts/run-stackarr.sh --build --data-root /srv/stackarr \
      --name stackarr-media --stack-name media --no-port

  # Point at existing storage and publish the UI on a different port
  ./scripts/run-stackarr.sh \
      --config-dir /srv/stackarr/config \
      --downloads-dir /srv/downloads \
      --media-dir /srv/media \
      --host-port 8080

Options:
  -n, --name NAME          Container name (default: stackarr)
  -s, --stack-name NAME    Stack name; namespaces containers and the Docker network
                           (default: derived from --name, else "default")
  -i, --image IMAGE        Image to run (default: stackarr:latest)
  -r, --data-root DIR      Directory holding config/, downloads/ and media/
                           (default: ./stackarr-data)
      --config-dir DIR     Config mount source (overrides --data-root/config)
      --downloads-dir DIR  Downloads mount source (overrides --data-root/downloads)
      --media-dir DIR      Media mount source (overrides --data-root/media)
      --network NAME       Docker network (default: stackarr-<stack-name>-net)
      --host-port PORT     Publish the web UI on PORT:5186 (default: 5186)
      --no-port            Do not publish any host port (tunnel-only deployments)
      --docker-socket PATH Docker socket to mount (default: /var/run/docker.sock)
      --restart POLICY     Docker restart policy (default: unless-stopped)
  -e, --env KEY=VALUE      Extra environment variable for the container (repeatable)
      --build              Build the image from the repo Dockerfile first
  -f, --force              Replace an existing container with the same name
  -h, --help               Show this help
EOF
}

die() {
    echo "error: $*" >&2
    exit 1
}

require_value() {
    [ -n "${2:-}" ] || die "option $1 requires a value"
}

while [ $# -gt 0 ]; do
    case "$1" in
        -n|--name)          require_value "$1" "${2:-}"; CONTAINER_NAME="$2"; shift 2 ;;
        -s|--stack-name)    require_value "$1" "${2:-}"; STACK_NAME="$2"; shift 2 ;;
        -i|--image)         require_value "$1" "${2:-}"; IMAGE="$2"; shift 2 ;;
        -r|--data-root)     require_value "$1" "${2:-}"; DATA_ROOT="$2"; shift 2 ;;
        --config-dir)       require_value "$1" "${2:-}"; CONFIG_DIR="$2"; shift 2 ;;
        --downloads-dir)    require_value "$1" "${2:-}"; DOWNLOADS_DIR="$2"; shift 2 ;;
        --media-dir)        require_value "$1" "${2:-}"; MEDIA_DIR="$2"; shift 2 ;;
        --network)          require_value "$1" "${2:-}"; NETWORK="$2"; shift 2 ;;
        --host-port)        require_value "$1" "${2:-}"; HOST_PORT="$2"; PUBLISH_PORT=1; shift 2 ;;
        --no-port)          PUBLISH_PORT=0; shift ;;
        --docker-socket)    require_value "$1" "${2:-}"; DOCKER_SOCKET="$2"; shift 2 ;;
        --restart)          require_value "$1" "${2:-}"; RESTART_POLICY="$2"; shift 2 ;;
        -e|--env)           require_value "$1" "${2:-}"; EXTRA_ENV+=("$2"); shift 2 ;;
        --build)            BUILD=1; shift ;;
        -f|--force)         FORCE=1; shift ;;
        -h|--help)          usage; exit 0 ;;
        --name=*)           CONTAINER_NAME="${1#*=}"; shift ;;
        --stack-name=*)     STACK_NAME="${1#*=}"; shift ;;
        --image=*)          IMAGE="${1#*=}"; shift ;;
        --data-root=*)      DATA_ROOT="${1#*=}"; shift ;;
        --config-dir=*)     CONFIG_DIR="${1#*=}"; shift ;;
        --downloads-dir=*)  DOWNLOADS_DIR="${1#*=}"; shift ;;
        --media-dir=*)      MEDIA_DIR="${1#*=}"; shift ;;
        --network=*)        NETWORK="${1#*=}"; shift ;;
        --host-port=*)      HOST_PORT="${1#*=}"; PUBLISH_PORT=1; shift ;;
        --docker-socket=*)  DOCKER_SOCKET="${1#*=}"; shift ;;
        --restart=*)        RESTART_POLICY="${1#*=}"; shift ;;
        --env=*)            EXTRA_ENV+=("${1#*=}"); shift ;;
        *)                  die "unknown option: $1 (try --help)" ;;
    esac
done

command -v docker >/dev/null 2>&1 || die "docker is not installed or not on PATH"
docker info >/dev/null 2>&1 || die "cannot talk to the Docker daemon (is it running?)"

# Derive a stack name from the container name when not given: stackarr-media -> media.
if [ -z "$STACK_NAME" ]; then
    case "$CONTAINER_NAME" in
        stackarr)   STACK_NAME="default" ;;
        stackarr-*) STACK_NAME="${CONTAINER_NAME#stackarr-}" ;;
        *)          STACK_NAME="$CONTAINER_NAME" ;;
    esac
fi
[ -n "$STACK_NAME" ] || die "could not derive a stack name; pass --stack-name"

[ -n "$NETWORK" ] || NETWORK="stackarr-${STACK_NAME}-net"

# Fill in the per-directory paths from --data-root where not explicitly overridden.
[ -n "$CONFIG_DIR" ]    || CONFIG_DIR="${DATA_ROOT}/config"
[ -n "$DOWNLOADS_DIR" ] || DOWNLOADS_DIR="${DATA_ROOT}/downloads"
[ -n "$MEDIA_DIR" ]     || MEDIA_DIR="${DATA_ROOT}/media"

# Docker needs absolute bind-mount sources, so create the dirs and canonicalise their paths.
abs_dir() {
    local dir="$1"
    mkdir -p -- "$dir" || die "could not create directory: $dir"
    (cd -- "$dir" && pwd)
}

CONFIG_DIR="$(abs_dir "$CONFIG_DIR")"
DOWNLOADS_DIR="$(abs_dir "$DOWNLOADS_DIR")"
MEDIA_DIR="$(abs_dir "$MEDIA_DIR")"

if [ "$BUILD" -eq 1 ]; then
    echo "==> Building $IMAGE from $REPO_ROOT/Dockerfile"
    docker build -t "$IMAGE" "$REPO_ROOT"
fi

if ! docker image inspect "$IMAGE" >/dev/null 2>&1; then
    die "image not found: $IMAGE (run with --build, or pull/build it first)"
fi

echo "==> Ensuring Docker network: $NETWORK"
docker network inspect "$NETWORK" >/dev/null 2>&1 || docker network create "$NETWORK" >/dev/null

if docker container inspect "$CONTAINER_NAME" >/dev/null 2>&1; then
    if [ "$FORCE" -eq 1 ]; then
        echo "==> Removing existing container: $CONTAINER_NAME"
        docker rm -f "$CONTAINER_NAME" >/dev/null
    else
        die "container '$CONTAINER_NAME' already exists (use --force to replace it, or 'docker start $CONTAINER_NAME')"
    fi
fi

RUN_ARGS=(
    --detach
    --name "$CONTAINER_NAME"
    --network "$NETWORK"
    --restart "$RESTART_POLICY"
    --volume "${DOCKER_SOCKET}:/var/run/docker.sock"
    --volume "${CONFIG_DIR}:/config"
    --volume "${DOWNLOADS_DIR}:/downloads"
    --volume "${MEDIA_DIR}:/media"
    --env "StackName=${STACK_NAME}"
)

if [ "$PUBLISH_PORT" -eq 1 ]; then
    RUN_ARGS+=(--publish "${HOST_PORT}:5186")
fi

for env_pair in "${EXTRA_ENV[@]:-}"; do
    [ -n "$env_pair" ] && RUN_ARGS+=(--env "$env_pair")
done

echo "==> Starting container: $CONTAINER_NAME"
docker run "${RUN_ARGS[@]}" "$IMAGE" >/dev/null

cat <<EOF

Stackarr is starting.

  Container : $CONTAINER_NAME
  Image     : $IMAGE
  Stack name: $STACK_NAME
  Network   : $NETWORK
  Config    : $CONFIG_DIR  -> /config
  Downloads : $DOWNLOADS_DIR  -> /downloads
  Media     : $MEDIA_DIR  -> /media
EOF

if [ "$PUBLISH_PORT" -eq 1 ]; then
    echo "  Web UI    : http://localhost:${HOST_PORT}"
else
    echo "  Web UI    : no host port published (reach it through the reverse proxy / tunnel)"
fi

cat <<EOF

First boot provisions the service containers and can take a few minutes.
Follow progress with:  docker logs -f $CONTAINER_NAME
EOF
