#!/usr/bin/env bash
# Builds a signed, expiring (15 days) release APK and publishes it as a GitHub release of 1letzgo/nordisch.
#   Tools/release/release_android.sh            # next build of the version in Tools/release/version.json → v1.0.<build>
#   Tools/release/release_android.sh --dry-run   # build only (current build number), no bump/tag/release
# Needs: Unity editor closed, gh logged in, ~/.config/nordisch/release.env (keystore, see CLAUDE.md "Release").
set -euo pipefail
cd "$(dirname "$0")/../.."

REPO=1letzgo/nordisch
UNITY=/Applications/Unity/Hub/Editor/6000.3.26f1/Unity.app/Contents/MacOS/Unity
ENV_FILE="$HOME/.config/nordisch/release.env"
DRY=0; [[ "${1:-}" == "--dry-run" ]] && DRY=1

[[ -f "$ENV_FILE" ]] || { echo "Missing $ENV_FILE (release keystore)"; exit 1; }
set -a; source "$ENV_FILE"; set +a

if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
  echo "Working tree not clean – commit first (the release is tagged on HEAD)."; git status --short | head; exit 1
fi

# Version: "1.0" (hand-edited in Tools/release/version.json) + running build number shared with iOS/TestFlight.
# versionName = 1.0.<build>, versionCode = 1000000 + build (above the old date-based codes).
if [[ $DRY == 1 ]]; then
  read -r marketing build < <(python3 -c "import json;v=json.load(open('Tools/release/version.json'));print(v['version'],v['build'])")
else
  read -r marketing build < <(python3 Tools/release/bump_build.py)
  git commit -q -m "Release $marketing.$build" Tools/release/version.json
fi
version="$marketing.$build"
code=$((1000000 + build))
expires=$(date -v+15d +%d.%m.%Y)
echo "Release v$version (versionCode $code), runs until $expires"

before=$(git status --porcelain --untracked-files=no)
log=/tmp/nordisch-release.log
"$UNITY" -batchmode -quit -projectPath . -buildTarget Android \
  -executeMethod LeGamez.Editor.BuildScript.BuildAndroidRelease -releaseVersion "$version" -versionCode "$code" \
  -logFile "$log" || { grep -E "error|Exception" "$log" | head -20; exit 1; }
apk="Builds/Android/Nordisch-$version.apk"
cp Builds/Android/Nordisch.apk "$apk"
echo "Built $apk ($(du -h "$apk" | cut -f1))"
[[ $DRY == 1 ]] && exit 0

# Unity rewrites an empty keystore name to "{inproject}: " – harmless, put the file back.
if git diff --unified=0 ProjectSettings/ProjectSettings.asset | grep -E '^[+-] ' | grep -qv 'AndroidKeystoreName'; then :; \
elif [[ -n "$(git diff --name-only ProjectSettings/ProjectSettings.asset)" ]]; then git checkout -- ProjectSettings/ProjectSettings.asset; fi
# Unity may have touched project files during the build (it should not) – never tag a dirty tree.
after=$(git status --porcelain --untracked-files=no)
[[ "$before" == "$after" ]] || { echo "Tracked files changed during the build:"; diff <(echo "$before") <(echo "$after"); exit 1; }
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
