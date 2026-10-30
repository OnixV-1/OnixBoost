#!/usr/bin/env bash
# Build OnixBoost + Setup.exe + Update.zip → dist/ → NL /opt/onixboost/public
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"          # .../onixboost/OnixBoost
PROJ="$(cd "$ROOT/.." && pwd)"                    # .../onixboost
SETUP="$PROJ/OnixBoost.Setup"
PUBLISH="$ROOT/publish"
OUT_DIR="$PROJ/dist"
VER="${1:-2.8.44}"
NL_HOST="${NL_HOST:-nl}"
NL_DIR="/opt/onixboost/public"
# Prefer :80 /onixboost (ISP-friendly); :8891 kept as mirror in version.json
NL_URL="http://85.137.63.156/onixboost"
NL_URL_LEGACY="http://85.137.63.156:8891"
NOTES="${NOTES:-Обновление OnixBoost $VER}"

mkdir -p "$OUT_DIR"

# Keep csproj versions in sync
sed -i -E "s|<Version>[0-9.]+</Version>|<Version>$VER</Version>|" \
  "$ROOT/OnixBoost.csproj" "$SETUP/OnixBoost.Setup.csproj"

echo "==> Publish OnixBoost $VER"
dotnet publish "$ROOT/OnixBoost.csproj" -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=false -p:Version="$VER" -o "$PUBLISH"

echo "==> Pre-ship verify"
python3 "$ROOT/packaging/verify-pre-ship.py"

echo "==> Pack payload.zip + OnixBoost-Update.zip"
rm -f "$SETUP/payload.zip" "$OUT_DIR/OnixBoost-Update.zip"
python3 - <<PY
import zipfile, os, shutil
src = r"$PUBLISH"
payload = r"$SETUP/payload.zip"
update = r"$OUT_DIR/OnixBoost-Update.zip"
with zipfile.ZipFile(payload, "w", zipfile.ZIP_DEFLATED) as z:
    for root, dirs, files in os.walk(src):
        for f in files:
            p = os.path.join(root, f)
            z.write(p, os.path.relpath(p, src))
shutil.copy2(payload, update)
print("payload:", os.path.getsize(payload), "bytes")
print("update zip:", os.path.getsize(update), "bytes")
PY

echo "==> Publish OnixBoost-Setup.exe"
dotnet publish "$SETUP/OnixBoost.Setup.csproj" -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:Version="$VER" -o "$SETUP/publish"

cp -f "$SETUP/publish/OnixBoost-Setup.exe" "$OUT_DIR/OnixBoost-Setup.exe"

echo "==> version.json"
python3 - <<PY
import json
ver = {
  "version": "$VER",
  "url": "$NL_URL/OnixBoost-Setup.exe",
  "zip": "$NL_URL/OnixBoost-Update.zip",
  "mirrors": [
    "$NL_URL",
    "$NL_URL_LEGACY",
  ],
  "notes": """$NOTES"""
}
path = r"$OUT_DIR/version.json"
open(path, "w", encoding="utf-8").write(json.dumps(ver, ensure_ascii=False, indent=2) + "\n")
print(open(path, encoding="utf-8").read())
PY

echo "==> Upload to NL $NL_HOST:$NL_DIR"
scp -o BatchMode=yes \
  "$OUT_DIR/OnixBoost-Setup.exe" \
  "$OUT_DIR/OnixBoost-Update.zip" \
  "$OUT_DIR/version.json" \
  "$NL_HOST:$NL_DIR/"

# keep soft landing page if present
ssh -o BatchMode=yes "$NL_HOST" "ls -lh $NL_DIR/"

echo "==> Verify NL"
curl -fsSL "$NL_URL/version.json"
echo
ls -lh "$OUT_DIR/OnixBoost-Setup.exe" "$OUT_DIR/OnixBoost-Update.zip"
echo "OK: NL $NL_URL ← $NL_DIR"
