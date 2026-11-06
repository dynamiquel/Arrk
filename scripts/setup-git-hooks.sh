#!/usr/bin/env bash
#
# Enable the repository's versioned Git hooks. Run once after cloning.
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(dirname -- "$script_dir")"

git -C "$repo_root" config core.hooksPath .githooks
chmod +x "$repo_root/.githooks/pre-push"

echo "Git hooks enabled: core.hooksPath=.githooks"
