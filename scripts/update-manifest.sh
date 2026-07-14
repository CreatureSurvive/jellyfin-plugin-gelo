#!/usr/bin/env bash
# Inject (or, if the version already exists, replace) a version entry in manifest.json.
# Run by the release workflow after a release asset has been uploaded.
#
# Usage: update-manifest.sh <version> <targetAbi> <sourceUrl> <sha256> <changelogFile>
#   <version>       e.g. 1.0.0.0
#   <targetAbi>     e.g. 10.11.0.0
#   <sourceUrl>     download URL of the release ZIP
#   <sha256>        SHA-256 of the ZIP at sourceUrl
#   <changelogFile> path to a text file whose contents become the version's changelog
set -euo pipefail

VERSION="$1"
TARGET="$2"
URL="$3"
SHA="$4"
CHANGELOG_FILE="$5"

MANIFEST="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/manifest.json"
[ -f "$MANIFEST" ] || { echo "manifest.json not found at $MANIFEST" >&2; exit 1; }
[ -f "$CHANGELOG_FILE" ] || { echo "changelog file not found: $CHANGELOG_FILE" >&2; exit 1; }

TS="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

tmp="$(mktemp)"
jq --arg ver "$VERSION" \
   --arg target "$TARGET" \
   --arg url "$URL" \
   --arg sha "$SHA" \
   --rawfile ch "$CHANGELOG_FILE" \
   --arg ts "$TS" '
  (.[0].versions // []) as $vs
  | ($vs | map(select(.version != $ver))) as $rest
  | .[0].versions = ($rest + [{
      "version": $ver,
      "changelog": ($ch | sub("\n$"; "")),
      "targetAbi": $target,
      "sourceUrl": $url,
      "checksum": $sha,
      "timestamp": $ts
    }])
' "$MANIFEST" > "$tmp"
mv "$tmp" "$MANIFEST"
echo "manifest.json updated for $VERSION"
