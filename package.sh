#!/usr/bin/env bash
# Stages the published plugin into a Jellyfin plugin folder with a generated meta.json,
# and (optionally) builds a flat release ZIP + SHA-256 for a plugin-repository manifest.
#
# Usage:
#   ./package.sh            # stage only (into dist/)
#   ./package.sh --deploy   # stage + copy into the local Jellyfin plugins dir (then restart jellyfin)
#   ./package.sh --zip      # stage + also write dist/<slug>_<version>.zip and .sha256
#
# Identity (name/version/guid/targetAbi/owner) is read from build.yaml so it stays in one place.
# Build first:  dotnet publish -c Release -r linux-x64 -o ./publish
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PUB="$ROOT/publish"

# Read a "key: \"value\"" (or unquoted) scalar from build.yaml.
readcfg() { grep -E "^$1:" "$ROOT/build.yaml" | head -1 | sed -E 's/^[^:]+: *"?([^"]*)"?.*$/\1/'; }

NAME="$(readcfg name)"
VERSION="$(readcfg version)"
GUID="$(readcfg guid)"
TARGET_ABI="$(readcfg targetAbi)"
OWNER="$(readcfg owner)"
ZIP_SLUG="$(printf '%s' "$NAME" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9]+/-/g; s/^-//; s/-$//')"

STAGE="$ROOT/dist/${NAME}_${VERSION}"
# Where --deploy copies into. Override with GELD_PLUGINS_DIR; no-op if it doesn't exist (e.g. in CI).
PLUGINS="${GELD_PLUGINS_DIR:-/opt/yams/config/jellyfin/data/plugins}"

DEPLOY=0
ZIP=0
for a in "$@"; do
  case "$a" in
    --deploy) DEPLOY=1 ;;
    --zip) ZIP=1 ;;
    -h|--help) sed -n '2,11p' "$0"; exit 0 ;;
    *) echo "unknown arg: $a (see --help)" >&2; exit 2 ;;
  esac
done

if [ ! -f "$PUB/Jellyfin.Plugin.Gelo.dll" ]; then
  echo "ERROR: publish output not found at $PUB — build first." >&2
  exit 1
fi

rm -rf "$STAGE"
mkdir -p "$STAGE"
cp -a "$PUB/." "$STAGE/"

# Strip native libs named *.dll — Jellyfin's loader globs every top-level *.dll as a managed
# assembly and would BadImageFormatException on these. The *.so equivalents stay (flat in the
# plugin root, found by NativeProbing) and are what loads on Linux.
rm -f "$STAGE/onnxruntime.dll" "$STAGE/onnxruntime_providers_shared.dll"

# Flatten the linux-x64 ONNX natives to the plugin root (where NativeProbing probes first), then
# drop the entire runtimes/ tree. This removes 240MB+ of multi-arch dead weight AND every remnant
# of the SQLite native (libe_sqlite3.so) — SQLite is owned by Jellyfin core, not us, so we must not
# ship a competing copy. Only libonnxruntime*.so survive.
if [ -d "$STAGE/runtimes/linux-x64/native" ]; then
  cp -f "$STAGE/runtimes/linux-x64/native/libonnxruntime.so" "$STAGE/" 2>/dev/null || true
  cp -f "$STAGE/runtimes/linux-x64/native/libonnxruntime_providers_shared.so" "$STAGE/" 2>/dev/null || true
fi
rm -rf "$STAGE/runtimes"
rm -f "$STAGE/libe_sqlite3.so"  # belt-and-suspenders: never ship our own sqlite native


cat > "$STAGE/meta.json" <<EOF
{
  "guid": "$GUID",
  "name": "$NAME",
  "owner": "$OWNER",
  "category": "MoviesAndShows",
  "version": "$VERSION",
  "targetAbi": "$TARGET_ABI",
  "description": "Semantic, context-aware home shelves and sub-ms item-to-item recommendations (Gelo).",
  "overview": "Two-tier recommendation engine: MiniLM embeddings + SIMD vector store + a per-user ML.NET ranker.",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%S.0000000Z)",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": []
}
EOF

echo "=== staged at $STAGE ==="
( cd "$STAGE" && find . -maxdepth 1 -type f | sort && echo "--- natives ---" && ( find runtimes -type f 2>/dev/null || true ) | sort )
echo "total size: $(du -sh "$STAGE" | cut -f1)"

if [ "$ZIP" -eq 1 ]; then
  # Flat ZIP (plugin files at the archive root) — the convention Jellyfin's plugin installer expects.
  ZIP_PATH="$ROOT/dist/${ZIP_SLUG}_${VERSION}.zip"
  ( cd "$STAGE" && rm -f "$ZIP_PATH" && zip -r -q "$ZIP_PATH" . )
  SHA="$(sha256sum "$ZIP_PATH" | cut -d' ' -f1)"
  printf '%s  %s\n' "$SHA" "$(basename "$ZIP_PATH")" > "$ZIP_PATH.sha256"
  echo "=== release zip ==="
  echo "path:   $ZIP_PATH"
  echo "sha256: $SHA"
fi

if [ "$DEPLOY" -eq 1 ]; then
  if [ ! -d "$PLUGINS" ]; then
    echo "=== deploy skipped: plugins dir $PLUGINS not found (set GELD_PLUGINS_DIR) ==="
  else
    rm -rf "$PLUGINS/${NAME}"_*
    cp -a "$STAGE" "$PLUGINS/"
    echo "=== DEPLOYED to $PLUGINS ==="
    echo "Restart to load:  docker restart jellyfin"
  fi
fi
