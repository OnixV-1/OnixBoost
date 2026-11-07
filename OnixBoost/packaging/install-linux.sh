#!/usr/bin/env bash
# Install OnixBoost into ~/.local (Fedora / Ubuntu / Debian) with menu icon + launcher.
set -euo pipefail
SRC="$(cd "$(dirname "$0")" && pwd)"
DEST="${ONIXBOOST_HOME:-$HOME/.local/share/onixboost}"
BIN_LINK="${XDG_BIN_HOME:-$HOME/.local/bin}/onixboost"
APP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICON_BASE="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor"

echo "==> install → $DEST"
mkdir -p "$DEST" "$(dirname "$BIN_LINK")" "$APP_DIR"

if command -v rsync >/dev/null 2>&1; then
  rsync -a --delete --exclude '.update-staging' "$SRC"/ "$DEST"/
else
  find "$DEST" -mindepth 1 -maxdepth 1 ! -name '.update-staging' -exec rm -rf {} + 2>/dev/null || true
  mkdir -p "$DEST"
  cp -a "$SRC"/. "$DEST"/
fi

# Prefer packaged Assets; fall back to source tree
ICON_SRC=""
for cand in \
  "$DEST/Assets/onixboost.png" \
  "$SRC/Assets/onixboost.png" \
  "$SRC/../Assets/onixboost.png" \
  "$DEST/onixboost.png"
do
  if [[ -f "$cand" ]]; then ICON_SRC="$cand"; break; fi
done

if [[ -n "$ICON_SRC" ]]; then
  mkdir -p "$DEST/Assets"
  # Avoid `cp: same file` abort under `set -e` when Assets already synced
  if [[ "$(realpath -m "$ICON_SRC")" != "$(realpath -m "$DEST/Assets/onixboost.png")" ]]; then
    cp -f "$ICON_SRC" "$DEST/Assets/onixboost.png"
  fi
  # Install into hicolor at several sizes (same PNG is fine for menu themes)
  for sz in 48 64 128 256 512; do
    d="$ICON_BASE/${sz}x${sz}/apps"
    mkdir -p "$d"
    cp -f "$DEST/Assets/onixboost.png" "$d/onixboost.png"
  done
  # Absolute path always works even if icon theme cache is stale
  ICON_ABS="$DEST/Assets/onixboost.png"
else
  echo "WARN: onixboost.png not found — menu may show a generic icon"
  ICON_ABS="onixboost"
fi

chmod +x "$DEST/OnixBoost" "$DEST/onixboost.sh" \
  "$DEST/dpi/bin/nfqws" "$DEST/dpi/core/sing-box" \
  "$DEST/dpi/bin/onixboost-nft" "$DEST/fix-caps.sh" "$DEST/install.sh" 2>/dev/null || true

# Launcher must resolve symlinks (see onixboost-linux.sh)
if [[ -f "$SRC/onixboost.sh" ]]; then
  install -m 755 "$SRC/onixboost.sh" "$DEST/onixboost.sh"
fi
if [[ -f "$SRC/fix-caps.sh" ]]; then
  install -m 755 "$SRC/fix-caps.sh" "$DEST/fix-caps.sh"
fi

ln -sfn "$DEST/onixboost.sh" "$BIN_LINK"

# Desktop entry: absolute Exec + Path + Icon so GNOME/KDE/XFCE start without a terminal PATH
DESK_IN="$DEST/onixboost.desktop"
[[ -f "$DESK_IN" ]] || DESK_IN="$SRC/onixboost.desktop"
{
  echo "[Desktop Entry]"
  echo "Type=Application"
  echo "Version=1.0"
  echo "Name=OnixBoost"
  echo "GenericName=Network routes"
  echo "Comment=Discord Soft + YouTube / Twitch / Telegram"
  echo "Exec=\"$DEST/onixboost.sh\" %u"
  echo "TryExec=$DEST/onixboost.sh"
  echo "Path=$DEST"
  echo "Icon=$ICON_ABS"
  echo "Terminal=false"
  echo "Categories=Network;Utility;"
  echo "Keywords=discord;dpi;vpn;youtube;twitch;telegram;onixboost;"
  echo "StartupNotify=true"
  echo "StartupWMClass=OnixBoost"
} > "$APP_DIR/onixboost.desktop"
chmod 644 "$APP_DIR/onixboost.desktop"

# Refresh caches so the icon appears immediately
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APP_DIR" 2>/dev/null || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -f -t "$ICON_BASE" 2>/dev/null || true
fi
# Mark desktop file trusted (Nautilus / some GNOME builds)
if command -v gio >/dev/null 2>&1; then
  gio set "$APP_DIR/onixboost.desktop" metadata::trusted true 2>/dev/null || true
fi

echo ""
echo "Menu: OnixBoost (icon → $ICON_ABS)"
echo "Run from menu or: $BIN_LINK"
echo ""
echo "==> fix-caps (setcap nfqws + sing-box) — после install caps сбрасываются"
echo "    Discord/Telegram TUN нужен cap_net_admin на sing-box."
if [[ -x "$DEST/fix-caps.sh" ]]; then
  if bash "$DEST/fix-caps.sh"; then
    echo "fix-caps: OK"
  else
    echo "WARN: fix-caps не завершился. В обычном терминале:"
    echo "  bash $DEST/fix-caps.sh"
    echo "Затем трей OnixBoost → Выход и откройте снова."
  fi
fi
