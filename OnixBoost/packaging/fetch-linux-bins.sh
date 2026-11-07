#!/usr/bin/env bash
# Download nfqws (zapret) + sing-box into dpi/ for Linux Soft + HY2.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BIN="$ROOT/dpi/bin"
CORE="$ROOT/dpi/core"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

mkdir -p "$BIN" "$CORE"

echo "==> sing-box (linux-amd64)"
# Must match Windows dpi/core schema (typed DNS + route.action). 1.11 rejects our client-*.json.
SING_VER="${SING_BOX_VERSION:-1.12.2}"
SING_URL="https://github.com/SagerNet/sing-box/releases/download/v${SING_VER}/sing-box-${SING_VER}-linux-amd64.tar.gz"
curl -fsSL "$SING_URL" -o "$TMP/sing-box.tgz"
tar -xzf "$TMP/sing-box.tgz" -C "$TMP"
find "$TMP" -type f -name sing-box -executable -print -quit | head -1 | while read -r f; do
  install -m 755 "$f" "$CORE/sing-box"
done
if [[ ! -x "$CORE/sing-box" ]]; then
  # fallback: any sing-box file
  f="$(find "$TMP" -type f -name sing-box | head -1)"
  install -m 755 "$f" "$CORE/sing-box"
fi
"$CORE/sing-box" version || true

echo "==> nfqws (bol-van/zapret)"
ZAP_VER="${ZAPRET_VERSION:-70.6}"
ZAP_URL="https://github.com/bol-van/zapret/releases/download/v${ZAP_VER}/zapret-v${ZAP_VER}.tar.gz"
# some tags use without 'v' in filename — try both
if ! curl -fsSL "$ZAP_URL" -o "$TMP/zapret.tgz"; then
  ZAP_URL="https://github.com/bol-van/zapret/releases/download/${ZAP_VER}/zapret-${ZAP_VER}.tar.gz"
  curl -fsSL "$ZAP_URL" -o "$TMP/zapret.tgz"
fi
tar -xzf "$TMP/zapret.tgz" -C "$TMP"
# Prefer GNU/Linux x86_64 — never pick android-* first
NFQ="$(find "$TMP" -type f -path '*/binaries/x86_64/nfqws' | head -1)"
if [[ -z "$NFQ" ]]; then
  NFQ="$(find "$TMP" -type f -path '*/binaries/x86/nfqws' | head -1)"
fi
[[ -n "$NFQ" ]] || { echo "nfqws not found in zapret archive"; exit 1; }
install -m 755 "$NFQ" "$BIN/nfqws"
file "$BIN/nfqws" || true
# Sanity: must be Linux ELF, not Android linker
if file "$BIN/nfqws" | grep -qi android; then
  echo "ERROR: picked Android nfqws"; exit 1
fi


echo "OK: $BIN/nfqws + $CORE/sing-box"
echo "Optional (skip pkexec each Soft start): sudo setcap 'cap_net_admin,cap_net_raw+ep' $BIN/nfqws"
