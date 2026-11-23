#!/usr/bin/env bash
set -euo pipefail
ROOT="${1:-.}"
ROOT="$(cd "$ROOT" && pwd)"
GIT_DIR="$(git -C "$ROOT" rev-parse --git-dir 2>/dev/null || true)"
[ -n "$GIT_DIR" ] || { echo "not a git repo: $ROOT" >&2; exit 0; }
case "$GIT_DIR" in /*) ;; *) GIT_DIR="$ROOT/$GIT_DIR" ;; esac
HOOKS="$GIT_DIR/hooks"
mkdir -p "$HOOKS"
SRC="$(cd "$(dirname "$0")" && pwd)"
install -m 0755 "$SRC/git-commit-msg-no-cursor.sh" "$HOOKS/commit-msg"
install -m 0755 "$SRC/git-prepare-commit-msg-no-cursor.sh" "$HOOKS/prepare-commit-msg"
install -m 0755 "$SRC/git-pre-push-no-cursor.sh" "$HOOKS/pre-push"
echo "installed no-cursor git hooks → $HOOKS"
