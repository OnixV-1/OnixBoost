#!/usr/bin/env bash
# One-time privilege setup for Soft (nfqws+nft) and Discord/Telegram TUN (sing-box).
# Run from a normal terminal as your user (will ask sudo password once):
#   bash ~/.local/share/onixboost/fix-caps.sh
set -euo pipefail

export PATH="/usr/sbin:/sbin:/usr/local/sbin:/usr/bin:/bin:${PATH:-}"

ROOT="$(cd "$(dirname "$0")" && pwd)"
NFQ="$ROOT/dpi/bin/nfqws"
SING="$ROOT/dpi/core/sing-box"
HELPER="$ROOT/dpi/bin/onixboost-nft"

die() { echo "ERROR: $*" >&2; exit 1; }

# Real desktop user (pkexec sets PKEXEC_UID; sudo sets SUDO_USER)
if [[ -n "${PKEXEC_UID:-}" ]]; then
  USER_NAME="$(id -un "$PKEXEC_UID" 2>/dev/null || true)"
elif [[ -n "${SUDO_USER:-}" && "${SUDO_USER}" != "root" ]]; then
  USER_NAME="$SUDO_USER"
else
  USER_NAME="$(id -un)"
fi

[[ -n "$USER_NAME" && "$USER_NAME" != "root" ]] || die \
  "не удалось определить обычного пользователя. Запустите без root: bash $ROOT/fix-caps.sh"

echo "OnixBoost: one-time setup"
echo "  user=$USER_NAME"
echo "  root=$ROOT"

[[ -f "$NFQ" ]] || die "nfqws не найден: $NFQ (переустановите: ./install.sh)"
[[ -f "$HELPER" ]] || die "onixboost-nft не найден: $HELPER (переустановите: ./install.sh)"

chmod +x "$NFQ" "$HELPER" 2>/dev/null || true
[[ -f "$SING" ]] && chmod +x "$SING" || true

run_root() {
  if [[ "$(id -u)" -eq 0 ]]; then
    "$@"
  else
    sudo "$@"
  fi
}

SETCAP=""
for c in /usr/sbin/setcap /sbin/setcap setcap; do
  if [[ "$c" == /* ]]; then
    [[ -x "$c" ]] && SETCAP="$c" && break
  else
    command -v "$c" >/dev/null 2>&1 && SETCAP="$(command -v "$c")" && break
  fi
done
[[ -n "$SETCAP" ]] || die "setcap не найден. Установите: sudo apt install libcap2-bin   или   sudo dnf install libcap"

echo "==> setcap nfqws"
if ! run_root "$SETCAP" 'cap_net_admin,cap_net_raw+ep' "$NFQ"; then
  die "setcap nfqws не удался (файл: $NFQ)"
fi

if [[ -x "$SING" ]]; then
  echo "==> setcap sing-box"
  run_root "$SETCAP" 'cap_net_admin,cap_net_raw+ep' "$SING" \
    || run_root "$SETCAP" 'cap_net_admin+ep' "$SING" \
    || echo "WARN: setcap sing-box не удался — Telegram/Discord TUN могут не работать"
fi

# Absolute path only — relative paths break sudoers
HELPER_ABS="$(readlink -f "$HELPER" 2>/dev/null || realpath "$HELPER" 2>/dev/null || echo "$HELPER")"
[[ "$HELPER_ABS" == /* ]] || die "не удалось получить абсолютный путь к onixboost-nft"

SUDOERS_FILE="/etc/sudoers.d/onixboost-${USER_NAME}"
TMP="$(mktemp)"
# shellcheck disable=SC2064
trap 'rm -f "$TMP"' EXIT
cat >"$TMP" <<EOF
# OnixBoost Soft — nft helper without password (fix-caps.sh)
# Generated for $USER_NAME — HELPER must stay at this path after updates.
${USER_NAME} ALL=(root) NOPASSWD: ${HELPER_ABS}
EOF

echo "==> sudoers $SUDOERS_FILE"
run_root install -m 440 "$TMP" "$SUDOERS_FILE"

VISUDO=""
for v in /usr/sbin/visudo /sbin/visudo visudo; do
  if [[ "$v" == /* ]]; then
    [[ -x "$v" ]] && VISUDO="$v" && break
  else
    command -v "$v" >/dev/null 2>&1 && VISUDO="$(command -v "$v")" && break
  fi
done
if [[ -n "$VISUDO" ]]; then
  if ! run_root "$VISUDO" -cf "$SUDOERS_FILE"; then
    run_root rm -f "$SUDOERS_FILE" || true
    die "visudo отклонил sudoers (проверьте имя пользователя / путь helper)"
  fi
else
  echo "WARN: visudo не найден — sudoers записан без проверки"
fi

GETCAP=""
for g in /usr/sbin/getcap /sbin/getcap getcap; do
  if [[ "$g" == /* ]]; then
    [[ -x "$g" ]] && GETCAP="$g" && break
  else
    command -v "$g" >/dev/null 2>&1 && GETCAP="$(command -v "$g")" && break
  fi
done

echo ""
echo "Checking:"
if [[ -n "$GETCAP" ]]; then
  "$GETCAP" "$NFQ" || true
  [[ -x "$SING" ]] && "$GETCAP" "$SING" || true
else
  echo "(getcap не найден — пропуск)"
fi

NFT_OK=0
if [[ "$(id -u)" -eq 0 ]]; then
  if command -v runuser >/dev/null 2>&1; then
    runuser -u "$USER_NAME" -- sudo -n "$HELPER_ABS" remove-all >/dev/null 2>&1 && NFT_OK=1
  fi
else
  sudo -n "$HELPER_ABS" remove-all >/dev/null 2>&1 && NFT_OK=1
fi

if [[ "$NFT_OK" -eq 1 ]]; then
  echo "sudo -n nft: OK"
else
  echo "WARN: sudo -n nft пока не OK — закройте OnixBoost и проверьте:"
  echo "  sudo -n $HELPER_ABS remove-all"
fi

echo ""
if [[ -x "$SING" ]]; then
  if [[ -n "$GETCAP" ]] && "$GETCAP" "$SING" 2>/dev/null | grep -qi cap_net_admin; then
    echo "sing-box CAP_NET_ADMIN: OK (Discord/Telegram TUN)"
  else
    echo "WARN: sing-box без cap_net_admin — Discord/Telegram TUN не поднимутся."
    echo "  sudo setcap 'cap_net_admin,cap_net_raw+ep' \"$SING\""
  fi
fi
echo ""
echo "OK — трей OnixBoost → Выход, затем откройте снова."
echo "После каждого install.sh снова нужен fix-caps.sh (setcap сбрасывается)."
