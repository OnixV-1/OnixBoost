#!/usr/bin/env bash
# Build self-contained OnixBoost for Fedora / Ubuntu / Debian (linux-x64).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REPO="$(cd "$ROOT/.." && pwd)"
OUT="${OUT:-$REPO/dist/linux-x64}"
export PATH="${PATH}:/root/.dotnet:$HOME/.dotnet:/usr/share/dotnet"

cd "$ROOT"
bash packaging/fetch-linux-bins.sh

VER="$(grep -oP '(?<=<Version>)[^<]+' OnixBoost.csproj | head -1)"
echo "==> publish OnixBoost $VER (net8.0 / linux-x64)"
rm -rf "$OUT"
dotnet publish OnixBoost.csproj \
  -c Release \
  -f net8.0 \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=false \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$OUT"

# Ensure Soft + core bins are executable and present
install -m 755 "$ROOT/dpi/bin/nfqws" "$OUT/dpi/bin/nfqws"
install -m 755 "$ROOT/dpi/bin/onixboost-nft" "$OUT/dpi/bin/onixboost-nft"
install -m 755 "$ROOT/dpi/core/sing-box" "$OUT/dpi/core/sing-box"
chmod +x "$OUT/OnixBoost" || true

# Icons for desktop menu (AvaloniaResource alone does not copy PNG to publish dir)
mkdir -p "$OUT/Assets"
install -m 644 "$ROOT/Assets/onixboost.png" "$OUT/Assets/onixboost.png"
install -m 644 "$ROOT/Assets/onixboost.png" "$OUT/onixboost.png"
install -m 644 "$ROOT/Assets/onixboost-tray.png" "$OUT/Assets/onixboost-tray.png" 2>/dev/null || true
install -m 644 "$ROOT/Assets/onixboost.ico" "$OUT/Assets/onixboost.ico" 2>/dev/null || true

# Desktop entry + helpers (fix-caps avoids broken pkexec password dialogs)
install -m 755 packaging/onixboost-linux.sh "$OUT/onixboost.sh"
install -m 644 packaging/onixboost.desktop "$OUT/onixboost.desktop"
install -m 755 packaging/install-linux.sh "$OUT/install.sh"
install -m 755 packaging/fix-caps.sh "$OUT/fix-caps.sh"

# Zip artifact (python zipfile — zip(1) may be missing)
ZIP="$REPO/dist/OnixBoost-linux-x64.zip"
rm -f "$ZIP"
python3 - <<PY
import zipfile, os
from pathlib import Path
out = Path("$OUT")
zip_path = Path("$ZIP")
with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
    for p in out.rglob("*"):
        if p.is_file():
            z.write(p, Path(out.name) / p.relative_to(out))
print("wrote", zip_path, "bytes", zip_path.stat().st_size)
PY
echo "OK: $OUT"
echo "OK: $ZIP"
ls -lh "$OUT/OnixBoost" "$OUT/dpi/bin/nfqws" "$OUT/dpi/core/sing-box" "$ZIP"
