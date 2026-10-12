#!/usr/bin/env python3
"""Fetch OpenStreetMap data for curated game areas via the Overpass API.

Usage:
    python3 Tools/osm/fetch_area.py            # all areas
    python3 Tools/osm/fetch_area.py oldenburg  # single area

Writes Data/<id>.osm.json (Overpass JSON with geometry inlined).
Data (c) OpenStreetMap contributors, ODbL.
"""
import json
import math
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
AREAS_FILE = Path(__file__).with_name("areas.json")
OUT_DIR = ROOT / "Data"
ENDPOINTS = [
    "https://overpass-api.de/api/interpreter",
    "https://overpass.kumi.systems/api/interpreter",
    "https://overpass.private.coffee/api/interpreter",
    "https://maps.mail.ru/osm/tools/overpass/api/interpreter",
]
USER_AGENT = "LeGamez-OSM-Fetcher/0.1 (game prototype)"


def bbox(lat: float, lon: float, size_m: float):
    half = size_m / 2.0
    dlat = half / 111_320.0
    dlon = half / (111_320.0 * math.cos(math.radians(lat)))
    return lat - dlat, lon - dlon, lat + dlat, lon + dlon


def build_query(s, w, n, e) -> str:
    b = f"{s:.6f},{w:.6f},{n:.6f},{e:.6f}"
    return f"""
[out:json][timeout:120];
(
  way["building"]({b});
  relation["building"]["type"="multipolygon"]({b});
  way["building:part"]({b});
  way["highway"]({b});
  way["railway"]({b});
  way["landuse"]({b});
  way["leisure"]({b});
  way["natural"]({b});
  way["amenity"="parking"]({b});
  way["waterway"]({b});
  way["water"]({b});
  way["barrier"]({b});
  relation["type"="multipolygon"]["landuse"]({b});
  relation["type"="multipolygon"]["natural"]({b});
  relation["type"="multipolygon"]["leisure"]({b});
  node["natural"="tree"]({b});
  node["name"]["tourism"]({b});
  node["name"]["historic"]({b});
  node["name"]["amenity"]({b});
  node["name"]["man_made"]({b});
  way["name"]["tourism"]({b});
  way["name"]["historic"]({b});
  way["man_made"="lighthouse"]({b});
  node["amenity"~"^(bench|fountain|bicycle_parking|waste_basket|cafe|restaurant|ice_cream|drinking_water|pub|bar|fast_food)$"]({b});
  way["amenity"="fountain"]({b});
  node["leisure"~"^(picnic_table|outdoor_seating)$"]({b});
  node["tourism"="artwork"]({b});
  node["historic"~"^(memorial|monument)$"]({b});
  node["highway"="street_lamp"]({b});
  node["man_made"="flagpole"]({b});
  node["barrier"="bollard"]({b});
);
out geom tags;
"""


def fetch(query: str) -> dict:
    data = urllib.parse.urlencode({"data": query}).encode()
    last_err = None
    for url in ENDPOINTS:
        for attempt in range(2):
            try:
                req = urllib.request.Request(url, data=data, headers={"User-Agent": USER_AGENT})
                with urllib.request.urlopen(req, timeout=180) as r:
                    return json.loads(r.read())
            except Exception as err:  # noqa: BLE001
                last_err = err
                time.sleep(5 * (attempt + 1))
    raise RuntimeError(f"Overpass failed: {last_err}")


def main(argv):
    areas = json.loads(AREAS_FILE.read_text())["areas"]
    wanted = set(argv[1:])
    OUT_DIR.mkdir(exist_ok=True)
    for a in areas:
        if wanted and a["id"] not in wanted:
            continue
        s, w, n, e = bbox(a["lat"], a["lon"], a["size_m"])
        print(f"[{a['id']}] bbox {s:.5f},{w:.5f},{n:.5f},{e:.5f} …", flush=True)
        result = fetch(build_query(s, w, n, e))
        result["legamez"] = {
            "id": a["id"], "name": a["name"], "center": [a["lat"], a["lon"]],
            "bbox": [s, w, n, e], "size_m": a["size_m"], "vehicles": a["vehicles"], "ground": a.get("ground", "grass"),
            "attribution": "© OpenStreetMap contributors (ODbL)",
        }
        out = OUT_DIR / f"{a['id']}.osm.json"
        out.write_text(json.dumps(result, ensure_ascii=False, separators=(",", ":")))
        counts = {}
        for el in result["elements"]:
            counts[el["type"]] = counts.get(el["type"], 0) + 1
        print(f"  -> {out.relative_to(ROOT)} {counts}")
        time.sleep(2)


if __name__ == "__main__":
    main(sys.argv)
