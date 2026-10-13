#!/usr/bin/env python3
"""Publishes map bundles to the GitHub release "maps" of 1letzgo/nordisch and updates its catalog maps.json.

Reads Builds/Maps/<platform>/index.json (written by Unity, LeGamez.Editor.MapBundles.BuildBatch).
Per map and platform a build is uploaded only if its content CRC changed; unchanged maps keep their version, so the
app offers an update only for maps that really changed. Builds for other app formats / Unity versions stay in the
catalog (older app versions keep finding theirs) unless --drop-old is given.

  python3 Tools/release/publish_maps.py [--dry-run] [--drop-old] [platform ...]   # default: ios android

Release "maps" is a pre-release, so GitHub's "latest release" (used by the Android app updater) never points at it.
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
TAG = "maps"
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
BASE_URL = f"https://github.com/{REPO}/releases/download/{TAG}/"


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


def ensure_release(dry):
    if gh("release", "view", TAG, check=False).returncode == 0:
        return
    print(f"Creating pre-release '{TAG}'")
    if not dry:
        gh("release", "create", TAG, "--prerelease", "--title", "Nordisch Karten",
           "--notes", "Kartendaten für die App (wird von der App selbst geladen, nicht manuell installieren).\n\n"
                      "Daten: © OpenStreetMap-Mitwirkende (ODbL) · 3D-Gebäude, Gelände, Luftbilder © LGLN (CC BY 4.0)")


def load_catalog():
    tmp = tempfile.mkdtemp()
    r = gh("release", "download", TAG, "-p", "maps.json", "-D", tmp, check=False)
    path = os.path.join(tmp, "maps.json")
    if r.returncode != 0 or not os.path.exists(path):
        print("No maps.json yet – starting a new catalog")
        return {"schema": 1, "maps": []}
    with open(path) as f:
        return json.load(f)


def release_assets():
    r = gh("release", "view", TAG, "--json", "assets", check=False)
    if r.returncode != 0:
        return set()
    return {a["name"] for a in json.loads(r.stdout)["assets"]}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("platforms", nargs="*", default=["ios", "android"])
    ap.add_argument("--dry-run", action="store_true", help="only show what would change, write Builds/Maps/maps.json")
    ap.add_argument("--drop-old", action="store_true", help="remove builds for other formats/Unity versions")
    a = ap.parse_args()

    indexes = []
    for p in a.platforms:
        path = os.path.join(ROOT, "Builds", "Maps", p, "index.json")
        if not os.path.exists(path):
            sys.exit(f"{path} missing – build the bundles first (publish_maps.sh)")
        with open(path) as f:
            indexes.append(json.load(f))

    ensure_release(a.dry_run)
    catalog = load_catalog()
    entries = {e["id"]: e for e in catalog.get("maps", [])}
    version = int(time.strftime("%Y%m%d%H%M"))
    stage = tempfile.mkdtemp()
    uploads = []  # local files named like their release asset

    for idx in indexes:
        platform, unity, fmt = idx["platform"], idx["unity"], idx["format"]
        for m in idx["maps"]:
            e = entries.setdefault(m["id"], {"id": m["id"], "builds": []})
            e.update(name=m["name"], lat=m["lat"], lon=m["lon"], vehicles=m["vehicles"], pois=m["pois"])
            builds = e.setdefault("builds", [])
            if a.drop_old:
                builds[:] = [b for b in builds if b["platform"] != platform or (b["format"] == fmt and b["unity"] == unity)]
            same = [b for b in builds if b["platform"] == platform and b["format"] == fmt and b["unity"] == unity]
            if same and same[0].get("crc") == m["crc"]:
                print(f"  {m['id']:<14} {platform:<8} unchanged (v{same[0]['version']})")
            else:
                name = f"{m['id']}-{platform}-f{fmt}-{version}.bundle"
                shutil.copyfile(m["file"], os.path.join(stage, name))
                uploads.append(os.path.join(stage, name))
                size = os.path.getsize(m["file"])
                builds[:] = [b for b in builds if b not in same]
                builds.append({"platform": platform, "format": fmt, "unity": unity, "version": version,
                               "url": BASE_URL + name, "size": size, "sha256": sha256(m["file"]), "crc": m["crc"]})
                print(f"  {m['id']:<14} {platform:<8} NEW v{version} ({size / 1048576:.0f} MB)")

            # Thumbnail: file name = content hash, so the app's cache notices a new picture.
            if os.path.exists(m["thumb"]):
                tname = f"{m['id']}-thumb-{sha256(m['thumb'])[:10]}.png"
                if not e.get("thumb", "").endswith("/" + tname):
                    shutil.copyfile(m["thumb"], os.path.join(stage, tname))
                    uploads.append(os.path.join(stage, tname))
                    e["thumb"] = BASE_URL + tname
                    print(f"  {m['id']:<14} thumbnail updated")

    catalog = {"schema": 1, "updated": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
               "maps": sorted(entries.values(), key=lambda e: -e.get("lat", 0))}
    out = os.path.join(ROOT, "Builds", "Maps", "maps.json")
    with open(out, "w") as f:
        json.dump(catalog, f, indent=1, ensure_ascii=False)
    print(f"Catalog: {out}")

    if a.dry_run:
        print(f"Dry run: {len(uploads)} file(s) would be uploaded")
        return

    for path in uploads:
        print(f"Uploading {os.path.basename(path)} …")
        gh("release", "upload", TAG, path, "--clobber", capture=False)
    gh("release", "upload", TAG, out, "--clobber")  # catalog last: never points at files that are not there yet

    # Drop assets no catalog entry references any more (replaced builds, old thumbnails).
    referenced = {"maps.json"}
    for e in catalog["maps"]:
        referenced.update(b["url"].rsplit("/", 1)[1] for b in e.get("builds", []))
        if e.get("thumb"):
            referenced.add(e["thumb"].rsplit("/", 1)[1])
    for name in sorted(release_assets() - referenced):
        print(f"Removing old asset {name}")
        gh("release", "delete-asset", TAG, name, "-y")
    print(f"https://github.com/{REPO}/releases/tag/{TAG}")


if __name__ == "__main__":
    main()
