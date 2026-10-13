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

from area_bounds import latlon_bbox, local_bounds
from area_mask import AreaMask

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
[out:json][timeout:300];
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
    for url in ENDPOINTS * 2:
        for attempt in range(2):
            try:
                req = urllib.request.Request(url, data=data, headers={"User-Agent": USER_AGENT})
                with urllib.request.urlopen(req, timeout=360) as r:
                    return json.loads(r.read())
            except Exception as err:  # noqa: BLE001
                last_err = err
                print(f"    {url}: {err}", flush=True)
                time.sleep(5 * (attempt + 1))
    raise RuntimeError(f"Overpass failed: {last_err}")


WATER_KEYS = {("natural", "water"), ("waterway", "river"), ("waterway", "canal"), ("waterway", "stream"),
              ("waterway", "riverbank"), ("landuse", "basin"), ("landuse", "reservoir")}


def prefilter(result, area, mask):
    """Large masked areas: drop what lies completely outside the mask (the importer cuts exactly; this only keeps
    the file small). Water and railways stay everywhere."""
    lat0, lon0 = area["lat"], area["lon"]
    m_lat, m_lon = 110574.0, 111320.0 * math.cos(math.radians(lat0))

    def any_inside(pts):
        if not pts:
            return False
        xs = [(p["lon"] - lon0) * m_lon for p in pts]
        zs = [(p["lat"] - lat0) * m_lat for p in pts]
        if mask.inside(xs, zs).any():
            return True
        # Long straight pieces may cross the mask between two outside vertices (conservative: segment bounds).
        return any(mask.any_in_rect(min(xs[i], xs[i + 1]), max(xs[i], xs[i + 1]), min(zs[i], zs[i + 1]), max(zs[i], zs[i + 1]))
                   for i in range(len(xs) - 1))

    kept = []
    for el in result["elements"]:
        tags = el.get("tags", {})
        if any((k, tags.get(k)) in WATER_KEYS for k in ("natural", "waterway", "landuse")) or "water" in tags or \
                tags.get("railway") in ("rail", "light_rail", "narrow_gauge"):
            kept.append(el)
            continue
        if el["type"] == "node":
            pts = [el]
        elif el["type"] == "way":
            pts = [p for p in el.get("geometry") or [] if p]
        else:
            pts = [p for m in el.get("members", []) for p in (m.get("geometry") or []) if p]
        if any_inside(pts):
            kept.append(el)
    print(f"  mask: kept {len(kept)} of {len(result['elements'])} elements", flush=True)
    result["elements"] = kept


TILE_M = 3000.0


def water_query(s, w, n, e) -> str:
    b = f"{s:.6f},{w:.6f},{n:.6f},{e:.6f}"
    return f"""
[out:json][timeout:600];
(
  way["natural"="water"]({b});
  way["water"]({b});
  way["waterway"~"^(river|canal|stream|riverbank)$"]({b});
  way["landuse"~"^(basin|reservoir)$"]({b});
  way["railway"~"^(rail|light_rail|narrow_gauge)$"]({b});
  relation["type"="multipolygon"]["natural"="water"]({b});
);
out geom tags;
"""


def fetch_tiled(area, mask):
    """Large masked areas: the full query only for 3 km tiles touching the mask (one query for 20 km times out),
    water and railways for the whole rectangle. Merged in Overpass order (nodes, ways, relations; by id)."""
    x0, x1, z0, z1 = local_bounds(area)
    lat0, lon0 = area["lat"], area["lon"]
    m_lat, m_lon = 110574.0, 111320.0 * math.cos(math.radians(lat0))
    merged = {}
    xs = [x0 + i * TILE_M for i in range(int(math.ceil((x1 - x0) / TILE_M)) + 1)]
    zs = [z0 + i * TILE_M for i in range(int(math.ceil((z1 - z0) / TILE_M)) + 1)]
    tiles = [(xa, min(xa + TILE_M, x1), za, min(za + TILE_M, z1)) for xa in xs[:-1] for za in zs[:-1]]
    tiles = [t for t in tiles if mask.any_in_rect(*t, 50)]
    for i, (xa, xb, za, zb) in enumerate(tiles):
        s, w, n, e = lat0 + za / m_lat, lon0 + xa / m_lon, lat0 + zb / m_lat, lon0 + xb / m_lon
        res = fetch(build_query(s, w, n, e))
        for el in res["elements"]:
            merged[(el["type"], el["id"])] = el
        print(f"    tile {i + 1}/{len(tiles)}: {len(res['elements'])} elements, {len(merged)} total", flush=True)
        time.sleep(1)
    s, w, n, e = latlon_bbox(area)
    res = fetch(water_query(s, w, n, e))
    for el in res["elements"]:
        merged.setdefault((el["type"], el["id"]), el)
    rank = {"node": 0, "way": 1, "relation": 2}
    out = dict(res)
    out["elements"] = [merged[k] for k in sorted(merged, key=lambda k: (rank.get(k[0], 3), k[1]))]
    return out


def main(argv):
    areas = json.loads(AREAS_FILE.read_text())["areas"]
    wanted = set(argv[1:])
    OUT_DIR.mkdir(exist_ok=True)
    for a in areas:
        if wanted and a["id"] not in wanted:
            continue
        s, w, n, e = latlon_bbox(a)
        print(f"[{a['id']}] bbox {s:.5f},{w:.5f},{n:.5f},{e:.5f} …", flush=True)
        mask = AreaMask.load(a["id"])
        if mask is not None:
            result = fetch_tiled(a, mask)
            prefilter(result, a, mask)
        else:
            result = fetch(build_query(s, w, n, e))
        result["legamez"] = {
            "id": a["id"], "name": a["name"], "center": [a["lat"], a["lon"]],
            "bbox": [s, w, n, e], "size_m": a["size_m"], "bounds": list(local_bounds(a)),
            "vehicles": a["vehicles"], "ground": a.get("ground", "grass"),
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
