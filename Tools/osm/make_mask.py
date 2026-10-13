#!/usr/bin/env python3
"""Build the area mask of a large area: where the world is built in detail, and where it is just terrain.

Usage:
    python3 Tools/osm/make_mask.py westerstede            # Overpass result is cached for a day
    python3 Tools/osm/make_mask.py westerstede --refresh  # fetch again

Needs "mask" in the area entry of areas.json, e.g.
    "mask": {"boundary": "Westerstede", "admin_level": 8, "step": 10,
             "settlement_buffer": 80, "road_buffer": 35, "roads": ["secondary", "tertiary", "unclassified"], ...}

Inside (see area_mask.py for the PNG encoding) =
    settlements – OSM landuse residential/commercial/industrial/retail and clusters of buildings (hamlets; single
    farms are left out), grown by settlement_buffer – plus corridors of road_buffer metres along the roads that connect
    them, all clipped to the municipality boundary (admin_level/boundary name). Outside stays terrain coloured by the
    aerial image; forests (32) and lakes (64) outside are marked so the coarse terrain shows them dark green / blue.

Writes Data/<id>.mask.png + Data/<id>.mask.json and prints the masked area in km².
Data (c) OpenStreetMap contributors, ODbL.
"""
import json
import math
import os
import sys
import tempfile
import time
import urllib.parse
import urllib.request
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

sys.path.insert(0, str(Path(__file__).resolve().parent))
from area_bounds import latlon_bbox, local_bounds  # noqa: E402
from area_mask import CORRIDOR, FOREST, SETTLEMENT, WATER  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
AREAS_FILE = Path(__file__).with_name("areas.json")
DATA = ROOT / "Data"
MARGIN = 60.0
ENDPOINTS = [
    "https://overpass-api.de/api/interpreter",
    "https://overpass.kumi.systems/api/interpreter",
    "https://overpass.private.coffee/api/interpreter",
    "https://maps.mail.ru/osm/tools/overpass/api/interpreter",
]

DEFAULTS = {
    "admin_level": 8,
    "step": 10,                      # m per mask pixel
    "landuse": ["residential", "commercial", "industrial", "retail"],
    "roads": ["motorway", "trunk", "primary", "secondary", "tertiary", "unclassified"],
    "settlement_buffer": 80,         # m around settlements
    "road_buffer": 35,               # m on each side of a connecting road
    "min_settlement": 15000,         # m² – smaller isolated landuse patches (single houses) are dropped …
    "cluster_radius": 100,           # … unless they belong to a cluster of ≥ cluster_buildings buildings
    "cluster_buildings": 12,         #     within cluster_radius (hamlets with several farmsteads)
    "fill_holes": 40000,             # m² – enclosed outside patches smaller than this become inside
    "include": [],                   # [[xMin, xMax, zMin, zMax], …] local rectangles always inside
}


def overpass(query, cache):
    if cache.exists() and time.time() - cache.stat().st_mtime < 86400:
        return json.loads(cache.read_text())
    data = urllib.parse.urlencode({"data": query}).encode()
    last = None
    for attempt in range(3):
        for url in ENDPOINTS:
            try:
                req = urllib.request.Request(url, data=data, headers={"User-Agent": "Nordisch-mask/0.1"})
                with urllib.request.urlopen(req, timeout=400) as r:
                    raw = r.read()
                cache.write_bytes(raw)
                return json.loads(raw)
            except Exception as err:  # noqa: BLE001 – Overpass mirrors time out a lot
                last = err
                print(f"    {url}: {err}", flush=True)
                time.sleep(5)
    raise RuntimeError(f"Overpass failed: {last}")


def build_query(cfg, s, w, n, e):
    b = f"{s:.6f},{w:.6f},{n:.6f},{e:.6f}"
    lu = "|".join(cfg["landuse"])
    roads = "|".join(cfg["roads"])
    return f"""
[out:json][timeout:600];
rel["boundary"="administrative"]["admin_level"="{cfg['admin_level']}"]["name"="{cfg['boundary']}"];
out geom;
(
  way["landuse"~"^({lu})$"]({b});
  relation["landuse"~"^({lu})$"]({b});
  way["highway"~"^({roads})(_link)?$"]({b});
  way["landuse"="forest"]({b});
  way["natural"="wood"]({b});
  relation["landuse"="forest"]({b});
  relation["natural"="wood"]({b});
  way["natural"="water"]({b});
  relation["natural"="water"]({b});
  way["landuse"~"^(basin|reservoir)$"]({b});
  way["waterway"="riverbank"]({b});
);
out geom;
way["building"]({b});
out center;
"""


class Raster:
    """Local frame raster, row 0 = north edge."""

    def __init__(self, area, step):
        lat0, lon0 = area["lat"], area["lon"]
        self.m_lat = 110574.0
        self.m_lon = 111320.0 * math.cos(math.radians(lat0))
        self.lat0, self.lon0 = lat0, lon0
        x0, x1, z0, z1 = local_bounds(area)
        self.x0, self.z0 = x0 - MARGIN, z0 - MARGIN
        self.step = step
        self.w = int(math.ceil((x1 + MARGIN - self.x0) / step))
        self.h = int(math.ceil((z1 + MARGIN - self.z0) / step))
        self.z1 = self.z0 + self.h * step

    def px(self, lat, lon):
        x = (lon - self.lon0) * self.m_lon
        z = (lat - self.lat0) * self.m_lat
        return ((x - self.x0) / self.step, (self.z1 - z) / self.step)

    def ring(self, geom):
        return [self.px(p["lat"], p["lon"]) for p in geom if p]

    def new(self):
        return Image.new("L", (self.w, self.h), 0)


def stitch(segments):
    """Joins member ways (lists of pixel points) into closed rings."""
    rings, open_ = [], []
    for s in segments:
        (rings if len(s) > 3 and s[0] == s[-1] else open_).append(list(s))
    while open_:
        ring = open_.pop(0)
        progress = True
        while progress and ring[0] != ring[-1]:
            progress = False
            for i, s in enumerate(open_):
                if s[0] == ring[-1]:
                    ring += s[1:]
                elif s[-1] == ring[-1]:
                    ring += s[::-1][1:]
                else:
                    continue
                open_.pop(i)
                progress = True
                break
        if len(ring) > 3:
            rings.append(ring)
    return rings


def fill_polygons(raster, elements, pred):
    """Rasterises closed ways and multipolygon relations matching pred (outer filled, inner cut)."""
    img = raster.new()
    draw = ImageDraw.Draw(img)
    holes = []
    for el in elements:
        t = el.get("tags", {})
        if not pred(t):
            continue
        if el["type"] == "way":
            g = raster.ring(el.get("geometry", []))
            if len(g) >= 3 and g[0] == g[-1]:
                draw.polygon(g, fill=1)
        elif el["type"] == "relation":
            outer = [raster.ring(m["geometry"]) for m in el.get("members", []) if m["type"] == "way" and m.get("geometry") and m.get("role") != "inner"]
            inner = [raster.ring(m["geometry"]) for m in el.get("members", []) if m["type"] == "way" and m.get("geometry") and m.get("role") == "inner"]
            for r in stitch(outer):
                draw.polygon(r, fill=1)
            holes += stitch(inner)
    for r in holes:
        draw.polygon(r, fill=0)
    return np.array(img, dtype=bool)


def grow(mask, metres, step):
    if metres <= 0:
        return mask
    return ndimage.distance_transform_edt(~mask) * step <= metres


def process_area(area, refresh):
    cfg = {**DEFAULTS, **area["mask"]}
    step = float(cfg["step"])
    r = Raster(area, step)
    s, w, n, e = latlon_bbox(area, MARGIN + 200)
    cache = Path(tempfile.gettempdir()) / f"nordisch_mask_{area['id']}.json"
    if refresh and cache.exists():
        cache.unlink()
    print(f"[{area['id']}] mask {r.w}x{r.h} px at {step:g} m, Overpass …", flush=True)
    osm = overpass(build_query(cfg, s, w, n, e), cache)
    els = osm["elements"]
    px_area = step * step

    # Municipality boundary.
    bound = None
    for el in els:
        if el["type"] == "relation" and el.get("tags", {}).get("boundary") == "administrative":
            bound = fill_polygons(r, [el], lambda t: True)
            break
    if bound is None:
        raise SystemExit(f"boundary '{cfg['boundary']}' (admin_level {cfg['admin_level']}) not found")
    print(f"  boundary: {bound.sum() * px_area / 1e6:.1f} km²")

    # Settlements: built-up landuse; tiny isolated patches only when they belong to a building cluster.
    landuse = set(cfg["landuse"])
    built = fill_polygons(r, [el for el in els if el["type"] != "relation" or el.get("tags", {}).get("type") == "multipolygon"],
                          lambda t: t.get("landuse") in landuse)
    dens = np.zeros((r.h, r.w), dtype=np.float32)
    nb = 0
    for el in els:
        if el["type"] == "way" and "building" in el.get("tags", {}) and "center" in el:
            c, row = r.px(el["center"]["lat"], el["center"]["lon"])
            if 0 <= int(c) < r.w and 0 <= int(row) < r.h:
                dens[int(row), int(c)] += 1
                nb += 1
    rad = int(round(cfg["cluster_radius"] / step))
    yy, xx = np.mgrid[-rad:rad + 1, -rad:rad + 1]
    disc = (xx * xx + yy * yy <= rad * rad).astype(np.float32)
    counts = ndimage.convolve(dens, disc, mode="constant")
    cluster = (counts >= cfg["cluster_buildings"]) & (dens > 0)
    cluster = grow(cluster, cfg["cluster_radius"] * 0.5, step)
    seeds = built | cluster
    labels, nl = ndimage.label(seeds)
    sizes = ndimage.sum(np.ones_like(labels), labels, index=np.arange(1, nl + 1)) * px_area
    keep_label = np.zeros(nl + 1, dtype=bool)
    keep_label[1:] = sizes >= cfg["min_settlement"]
    # A component counts if it is big enough or touches a building cluster.
    touched = np.unique(labels[cluster & (labels > 0)])
    keep_label[touched] = True
    settle = keep_label[labels]
    print(f"  {nb} buildings, {nl} settlement patches, kept {int(keep_label.sum())}; core {settle.sum() * px_area / 1e6:.1f} km²")
    settle = grow(settle, cfg["settlement_buffer"], step)

    # Corridors along the connecting roads.
    roads_img = r.new()
    draw = ImageDraw.Draw(roads_img)
    road_km = 0.0
    classes = set(cfg["roads"])
    for el in els:
        hw = el.get("tags", {}).get("highway")
        if el["type"] != "way" or hw is None or hw.replace("_link", "") not in classes:
            continue
        g = r.ring(el.get("geometry", []))
        if len(g) >= 2:
            draw.line(g, fill=1, width=1)
            road_km += sum(math.dist(g[i], g[i + 1]) for i in range(len(g) - 1)) * step / 1000
    roads = np.array(roads_img, dtype=bool)
    corridor = grow(roads, cfg["road_buffer"], step)

    # Always-detailed rectangles ("include": [[west, east, south, north], …] in local metres, e.g. the former town map
    # with its parks and woods, so it stays exactly as before).
    incl = np.zeros_like(bound)
    for x0, x1, z0, z1 in cfg.get("include", []):
        c0, c1 = int((x0 - r.x0) / step), int(math.ceil((x1 - r.x0) / step))
        r0, r1 = int((r.z1 - z1) / step), int(math.ceil((r.z1 - z0) / step))
        incl[max(r0, 0):r1, max(c0, 0):c1] = True
    settle |= incl
    inside = ((settle | corridor) & bound) | incl
    # Small enclosed gaps (a field between two arms of a village) are filled.
    gaps, ng = ndimage.label(~inside)
    if ng:
        gsizes = ndimage.sum(np.ones_like(gaps), gaps, index=np.arange(1, ng + 1)) * px_area
        border = np.unique(np.concatenate([gaps[0], gaps[-1], gaps[:, 0], gaps[:, -1]]))
        small = np.zeros(ng + 1, dtype=bool)
        small[1:] = gsizes < cfg["fill_holes"]
        small[border] = False
        small[0] = False
        inside |= small[gaps]
    settle &= inside

    forest = fill_polygons(r, els, lambda t: t.get("landuse") == "forest" or t.get("natural") == "wood")
    water = fill_polygons(r, els, lambda t: t.get("natural") == "water" or t.get("landuse") in ("basin", "reservoir")
                          or t.get("waterway") == "riverbank")
    out = np.zeros((r.h, r.w), dtype=np.uint8)
    out[forest] = FOREST
    out[water] = WATER
    out[inside] = CORRIDOR
    out[settle] = SETTLEMENT

    DATA.mkdir(exist_ok=True)
    Image.fromarray(out, "L").save(DATA / f"{area['id']}.mask.png", optimize=True)
    km2 = inside.sum() * px_area / 1e6
    nb_in = int((dens * inside).sum())
    meta = {"xMin": r.x0, "zMin": r.z0, "step": step, "width": r.w, "height": r.h,
            "inside_km2": round(km2, 2), "settlement_km2": round(settle.sum() * px_area / 1e6, 2),
            "corridor_km2": round((inside & ~settle).sum() * px_area / 1e6, 2), "road_km": round(road_km, 1), "osm_buildings_inside": nb_in,
            "source": "OpenStreetMap (ODbL)", "config": cfg}
    (DATA / f"{area['id']}.mask.json").write_text(json.dumps(meta, indent=1, ensure_ascii=False))
    print(f"  -> Data/{area['id']}.mask.png: inside {km2:.1f} km² (settlements {meta['settlement_km2']} km², "
          f"corridors {meta['corridor_km2']} km² along {road_km:.0f} km of road), forest outside "
          f"{(forest & ~inside).sum() * px_area / 1e6:.1f} km², rectangle {r.w * r.h * px_area / 1e6:.0f} km², "
          f"{nb_in} of {int((dens * bound).sum())} OSM buildings of the municipality inside", flush=True)


def main(argv):
    areas = json.loads(AREAS_FILE.read_text())["areas"]
    refresh = "--refresh" in argv
    wanted = {a for a in argv[1:] if not a.startswith("--")}
    for a in areas:
        if (not wanted or a["id"] in wanted) and "mask" in a:
            process_area(a, refresh)


if __name__ == "__main__":
    main(sys.argv)
