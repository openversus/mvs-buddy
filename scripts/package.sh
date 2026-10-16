#!/bin/bash
# Publishes the buddy as the one file people double-click, and packs it for each platform:
#   scripts/package.sh            -> artifacts/MvsBuddy-<version>-linux-x64.tar.gz and MvsBuddy-<version>-win-x64.zip
#   scripts/package.sh linux      -> the tar.gz only
#   scripts/package.sh windows    -> the zip only
# The version is the repository's VERSION file. Each archive holds the executable alone: on Linux it keeps its execute
# bit, and the buddy adds itself to the application menu the first time it runs. The native library each needs comes
# from scripts/build-native.sh (linux: the Steam Runtime build, so the result runs on a Steam Deck).
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
VERSION=$(tr -d '[:space:]' < "$ROOT/VERSION")
OUT=$ROOT/artifacts
mkdir -p "$OUT/publish"

publish() {
    local rid=$1
    local dir=$ROOT/artifacts/publish/$rid
    rm -rf "$dir"
    # The build's own output (trim warnings from the libraries) goes to the log beside the folder; the folder is the result.
    dotnet publish "$ROOT/src/MvsBuddy.App" -c Release -r "$rid" -o "$dir" --nologo -v q > "$dir.log" 2>&1 \
        || { cat "$dir.log" >&2; echo "dotnet publish failed for $rid (see $dir.log)" >&2; exit 1; }
    echo "$dir"
}

want=${1:-all}
if [ "$want" = all ] || [ "$want" = linux ]; then
    dir=$(publish linux-x64)
    [ -f "$dir/MvsBuddy" ] || { echo "No MvsBuddy in $dir" >&2; exit 1; }
    chmod 755 "$dir/MvsBuddy"
    tar -C "$dir" --owner=0 --group=0 -czf "$OUT/MvsBuddy-$VERSION-linux-x64.tar.gz" MvsBuddy
    echo "$OUT/MvsBuddy-$VERSION-linux-x64.tar.gz ($(du -h "$OUT/MvsBuddy-$VERSION-linux-x64.tar.gz" | cut -f1))"
fi
if [ "$want" = all ] || [ "$want" = windows ]; then
    dir=$(publish win-x64)
    [ -f "$dir/MvsBuddy.exe" ] || { echo "No MvsBuddy.exe in $dir" >&2; exit 1; }
    rm -f "$OUT/MvsBuddy-$VERSION-win-x64.zip"
    (cd "$dir" && zip -q -X "$OUT/MvsBuddy-$VERSION-win-x64.zip" MvsBuddy.exe)
    echo "$OUT/MvsBuddy-$VERSION-win-x64.zip ($(du -h "$OUT/MvsBuddy-$VERSION-win-x64.zip" | cut -f1))"
fi
