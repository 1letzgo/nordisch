#!/usr/bin/env bash
# Builds a signed, expiring (15 days) release APK and publishes it as a GitHub release of 1letzgo/nordisch.
#   Tools/release/release_android.sh            # version = today's date (2026.10.13, then 2026.10.13.2, …)
#   Tools/release/release_android.sh --dry-run   # build only, no tag/release
# Needs: Unity editor closed, gh logged in, ~/.config/nordisch/release.env (keystore, see CLAUDE.md "Release").
set -euo pipefail
cd "$(dirname "$0")/../.."

REPO=1letzgo/nordisch
UNITY=/Applications/Unity/Hub/Editor/6000.3.26f1/Unity.app/Contents/MacOS/Unity
ENV_FILE="$HOME/.config/nordisch/release.env"
DRY=0; [[ "${1:-}" == "--dry-run" ]] && DRY=1

[[ -f "$ENV_FILE" ]] || { echo "Missing $ENV_FILE (release keystore)"; exit 1; }
set -a; source "$ENV_FILE"; set +a

if [[ -n "$(git status --porcelain)" ]]; then
  echo "Working tree not clean – commit first (the release is tagged on HEAD)."; git status --short | head; exit 1
fi

# Version: date, with a running suffix for several releases a day. Version code: 10-minute steps since 2025.
base=$(date +%Y.%m.%d)
version=$base; n=1
while git rev-parse -q --verify "refs/tags/v$version" >/dev/null || gh release view "v$version" -R "$REPO" >/dev/null 2>&1; do
  n=$((n + 1)); version="$base.$n"
done
code=$(( ($(date +%s) - $(date -j -f %Y-%m-%d 2025-01-01 +%s)) / 600 ))
expires=$(date -v+15d +%d.%m.%Y)
echo "Release v$version (versionCode $code), runs until $expires"

log=/tmp/nordisch-release.log
"$UNITY" -batchmode -quit -projectPath . -buildTarget Android \
  -executeMethod LeGamez.Editor.BuildScript.BuildAndroidRelease -releaseVersion "$version" -versionCode "$code" \
  -logFile "$log" || { grep -E "error|Exception" "$log" | head -20; exit 1; }
apk="Builds/Android/Nordisch-$version.apk"
cp Builds/Android/Nordisch.apk "$apk"
echo "Built $apk ($(du -h "$apk" | cut -f1))"
[[ $DRY == 1 ]] && exit 0

# Unity may have touched project files during the build (it should not) – never tag a dirty tree.
git status --porcelain | grep -q . && { echo "Build changed tracked files:"; git status --short; exit 1; }
git push origin HEAD
git tag "v$version" && git push origin "v$version"
gh release create "v$version" "$apk" -R "$REPO" --latest --title "Nordisch $version" --notes "$(cat <<EOF
Android-Testversion vom $(date +%d.%m.%Y). **Läuft bis $expires** – danach bitte die neue Version laden.

Installation: \`Nordisch-$version.apk\` auf dem Handy öffnen und „Installation aus dieser Quelle zulassen“ bestätigen.
Neue Versionen lassen sich direkt über die alte installieren (gleiche Signatur, Spielstand bleibt).

Daten: © OpenStreetMap-Mitwirkende (ODbL) · 3D-Gebäude, Gelände, Luftbilder © LGLN (CC BY 4.0) · Figuren: Kenney (CC0)
EOF
)"
echo "https://github.com/$REPO/releases/tag/v$version"
