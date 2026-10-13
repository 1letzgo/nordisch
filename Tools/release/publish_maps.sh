#!/usr/bin/env bash
# Builds the map bundles (iOS + Android) and publishes changed maps to the GitHub release "maps" (catalog maps.json).
#   Tools/release/publish_maps.sh                  # all areas of Assets/Generated/AreaCatalog.asset
#   Tools/release/publish_maps.sh jever westerstede
#   Tools/release/publish_maps.sh --dry-run        # build + show what would change, upload nothing
#   Tools/release/publish_maps.sh --drop-old       # also drop builds for older app formats / Unity versions
# Needs: Unity editor closed, gh logged in, areas imported (ImportAllBatch / ImportAreaBatch, thumbnails rendered).
# Publish maps BEFORE releasing an app with a new MapStore.Format or Unity version – otherwise its users find no maps.
set -euo pipefail
cd "$(dirname "$0")/../.."

UNITY=/Applications/Unity/Hub/Editor/6000.3.26f1/Unity.app/Contents/MacOS/Unity
PY_ARGS=(); AREAS=()
for a in "$@"; do
  case "$a" in
    --dry-run|--drop-old) PY_ARGS+=("$a") ;;
    *) AREAS+=("$a") ;;
  esac
done
AREA_ARGS=(); [[ ${#AREAS[@]} -gt 0 ]] && AREA_ARGS=(-areas "$(IFS=,; echo "${AREAS[*]}")")

for target in iOS Android; do
  log=/tmp/nordisch-maps-$target.log
  echo "Building $target map bundles …"
  # Unity batchmode occasionally dies with exit 139 during asset import – one retry.
  for try in 1 2; do
    if "$UNITY" -batchmode -quit -projectPath . -buildTarget "$target" \
        -executeMethod LeGamez.Editor.MapBundles.BuildBatch ${AREA_ARGS[@]+"${AREA_ARGS[@]}"} -logFile "$log"; then break; fi
    [[ $try == 2 ]] && { grep -E "\[Maps\]|error|Exception" "$log" | head -20; exit 1; }
    echo "  Unity failed, retrying …"
  done
  grep "\[Maps\]" "$log" || true
done

python3 Tools/release/publish_maps.py ${PY_ARGS[@]+"${PY_ARGS[@]}"} ios android
