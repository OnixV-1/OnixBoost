#!/usr/bin/env bash
# Resolve symlinks so menu / ~/.local/bin/onixboost finds the real install dir.
set -euo pipefail
SOURCE="${BASH_SOURCE[0]}"
while [[ -L "$SOURCE" ]]; do
  DIR="$(cd -P "$(dirname "$SOURCE")" && pwd)"
  LINK="$(readlink "$SOURCE")"
  if [[ "$LINK" == /* ]]; then
    SOURCE="$LINK"
  else
    SOURCE="$DIR/$LINK"
  fi
done
DIR="$(cd -P "$(dirname "$SOURCE")" && pwd)"
cd "$DIR"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-0}"
export LANG="${LANG:-${LC_ALL:-en_US.UTF-8}}"
# GUI .desktop PATH often omits /usr/sbin — then getcap/setcap "missing" and Soft loops on password
export PATH="/usr/sbin:/sbin:/usr/local/sbin:$DIR:$HOME/.local/bin:/usr/local/bin:/usr/bin:/bin:$PATH"
exec "$DIR/OnixBoost" "$@"
