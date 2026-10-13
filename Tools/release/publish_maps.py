#!/usr/bin/env python3
"""Publishes map bundles to GitHub releases of 1letzgo/nordisch and updates the catalog maps.json.

Reads Builds/Maps/index.json (written by Unity, LeGamez.Editor.MapExport.ExportBatch). Format 5: each area consists of
area.bin (area data) + one file per 1 km region (r<rx>_<rz>.bin) – the same files for iOS and Android ("platform": "any"),
independent of the Unity version. Files are uploaded per area to the pre-release "maps-<id>" under content-addressed
names, so unchanged regions keep their URL and are neither uploaded again nor downloaded again by the app (it compares
sha256 per file). The catalog lives in the pre-release "maps":
  https://github.com/1letzgo/nordisch/releases/download/maps/maps.json
Builds for other app formats / Unity versions stay in the catalog (older apps keep finding theirs) unless --drop-old.

  python3 Tools/release/publish_maps.py [--dry-run] [--drop-old]

All map releases are pre-releases, so GitHub's "latest release" (used by the Android app updater) never points at them.
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

REPO = "1letzgo/nordisch"
CATALOG_TAG = "maps"
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
NOTES = ("Kartendaten für die App (wird von der App selbst geladen, nicht manuell installieren).\n\n"
         "Daten: © OpenStreetMap-Mitwirkende (ODbL) · 3D-Gebäude, Gelände, Luftbilder © LGLN (CC BY 4.0)")


def url_of(tag, name):
    return f"https://github.com/{REPO}/releases/download/{tag}/{name}"


def gh(*args, check=True, capture=True):
    r = subprocess.run(["gh", *args, "-R", REPO], capture_output=capture, text=True)
    if check and r.returncode != 0:
        sys.exit(f"gh {' '.join(args)} failed:\n{r.stderr}")
    return r


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def ensure_release(tag, title, dry):
    if gh("release", "view", tag, check=False).returncode == 0:
        return
    print(f"Creating pre-release '{tag}'")
    if not dry:
        gh("release", "create", tag, "--prerelease", "--title", title, "--notes", NOTES)


def load_catalog():
    tmp = tempfile.mkdtemp()
    r = gh("release", "download", CATALOG_TAG, "-p", "maps.json", "-D", tmp, check=False)
    path = os.path.join(tmp, "maps.json")
    if r.returncode != 0 or not os.path.exists(path):
        print("No maps.json yet – starting a new catalog")
        return {"schema": 1, "maps": []}
    with open(path) as f:
        return json.load(f)


def release_assets(tag):
    r = gh("release", "view", tag, "--json", "assets", check=False)
    if r.returncode != 0:
        return set()
    return {a["name"] for a in json.loads(r.stdout)["assets"]}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true", help="only show what would change, write Builds/Maps/maps.json")
    ap.add_argument("--drop-old", action="store_true", help="remove builds for other formats/Unity versions")
    a = ap.parse_args()

    path = os.path.join(ROOT, "Builds", "Maps", "index.json")
    if not os.path.exists(path):
        sys.exit(f"{path} missing – export the maps first (publish_maps.sh)")
    with open(path) as f:
        indexes = [json.load(f)]

    ensure_release(CATALOG_TAG, "Nordisch Karten", a.dry_run)
    catalog = load_catalog()
    entries = {e["id"]: e for e in catalog.get("maps", [])}
    version = int(time.strftime("%Y%m%d%H%M"))
    stage = tempfile.mkdtemp()
    uploads = {}  # tag -> [local staged paths]

    for idx in indexes:
        platform, unity, fmt = idx.get("platform", "any"), idx.get("unity", ""), idx["format"]
        for m in idx["maps"]:
            tag = f"maps-{m['id']}"
            e = entries.setdefault(m["id"], {"id": m["id"], "builds": []})
            e.update(name=m["name"], lat=m["lat"], lon=m["lon"], vehicles=m["vehicles"], pois=m["pois"])
            builds = e.setdefault("builds", [])
            if a.drop_old:
                builds[:] = [b for b in builds if b["platform"] != platform or (b["format"] == fmt and b["unity"] == unity)]
            same = [b for b in builds if b["platform"] == platform and b["format"] == fmt and b["unity"] == unity]
            old_files = {f["name"]: f for f in (same[0].get("files", []) if same else [])}

            files, changed = [], 0
            for f in m["files"]:
                old = old_files.get(f["name"])
                digest = sha256(f["path"])
                if old and old.get("sha256") == digest:
                    files.append(old)  # unchanged content: keep URL, no upload, no re-download in the app
                    continue
                stem = f["name"].rsplit(".", 1)[0]
                name = f"{m['id']}-f{fmt}-{stem}-{digest[:12]}.bin"
                shutil.copyfile(f["path"], os.path.join(stage, name))
                uploads.setdefault(tag, []).append(os.path.join(stage, name))
                entry = {"name": f["name"], "url": url_of(tag, name), "size": os.path.getsize(f["path"]),
                         "sha256": digest}
                if f.get("region"):
                    entry["region"] = f["region"]
                files.append(entry)
                changed += 1
            removed = len(set(old_files) - {f["name"] for f in m["files"]})
            start = m.get("start", [])
            if changed or removed or not same or same[0].get("start") != start:
                builds[:] = [b for b in builds if b not in same]
                area = files[0]
                builds.append({"platform": platform, "format": fmt, "unity": unity, "version": version,
                               "url": area["url"], "size": sum(f["size"] for f in files), "sha256": area["sha256"],
                               "files": files, "start": start})
                print(f"  {m['id']:<14} {platform:<8} v{version}: {changed} of {len(files)} files new, {removed} removed "
                      f"({sum(f['size'] for f in files) / 1048576:.0f} MB total)")
            else:
                print(f"  {m['id']:<14} {platform:<8} unchanged (v{same[0]['version']})")

            # Thumbnail (catalog release): file name = content hash, so the app's cache notices a new picture.
            if os.path.exists(m["thumb"]):
                tname = f"{m['id']}-thumb-{sha256(m['thumb'])[:10]}.png"
                if not e.get("thumb", "").endswith("/" + tname):
                    shutil.copyfile(m["thumb"], os.path.join(stage, tname))
                    uploads.setdefault(CATALOG_TAG, []).append(os.path.join(stage, tname))
                    e["thumb"] = url_of(CATALOG_TAG, tname)
                    print(f"  {m['id']:<14} thumbnail updated")

    catalog = {"schema": 1, "updated": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
               "maps": sorted(entries.values(), key=lambda e: -e.get("lat", 0))}
    out = os.path.join(ROOT, "Builds", "Maps", "maps.json")
    with open(out, "w") as f:
        json.dump(catalog, f, indent=1, ensure_ascii=False)
    print(f"Catalog: {out}")

    if a.dry_run:
        print(f"Dry run: {sum(len(v) for v in uploads.values())} file(s) would be uploaded")
        return

    for tag, paths in uploads.items():
        ensure_release(tag, f"Nordisch Karte {tag[5:]}" if tag != CATALOG_TAG else "Nordisch Karten", False)
        for path in paths:
            print(f"Uploading {tag}/{os.path.basename(path)} …")
            gh("release", "upload", tag, path, "--clobber", capture=False)
    gh("release", "upload", CATALOG_TAG, out, "--clobber")  # catalog last: never points at files that are not there yet

    # Drop assets no catalog entry references any more (replaced regions/builds, old thumbnails).
    referenced = {}
    for e in catalog["maps"]:
        urls = [e.get("thumb", "")]
        for b in e.get("builds", []):
            urls.append(b.get("url", ""))
            urls += [f["url"] for f in b.get("files", [])]
        for u in urls:
            if "/releases/download/" in u:
                tag, name = u.split("/releases/download/", 1)[1].split("/", 1)
                referenced.setdefault(tag, set()).add(name)
    referenced.setdefault(CATALOG_TAG, set()).add("maps.json")
    for tag in set(referenced) | {f"maps-{e['id']}" for e in catalog["maps"]}:
        for name in sorted(release_assets(tag) - referenced.get(tag, set())):
            print(f"Removing old asset {tag}/{name}")
            gh("release", "delete-asset", tag, name, "-y")
    print(f"https://github.com/{REPO}/releases/tag/{CATALOG_TAG}")


if __name__ == "__main__":
    main()
