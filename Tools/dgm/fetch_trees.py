#!/usr/bin/env python3
"""Detect individual trees from LGLN laser data (DOM1 surface − DGM1 terrain) for a game area.

Usage:
    python3 Tools/dgm/fetch_trees.py              # all areas
    python3 Tools/dgm/fetch_trees.py westerstede  # single area

Method (per 1 km tile, 1 m raster):
    nDOM = DOM1 − DGM1                         height of everything above ground
    mask buildings (LoD2 footprints, +1.5 m)   so only vegetation remains
    smooth (Gaussian σ≈1 m), find local maxima ≥ MIN_HEIGHT within a 5 m window
    crown radius = mean distance at which nDOM falls below 55 % of the top, in 8 directions

Output Data/<id>.trees.json: {"trees": [[x, z, height, crownRadius], ...]} in local metres
(x = east, z = north, same frame as the importer). Requires Data/<id>.lod2.json for the mask.

Data: DOM1 / DGM1 © LGLN, CC BY 4.0.
"""
import io
import json
import math
import sys
import urllib.request
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lod2"))
from fetch_lod2 import LocalFrame, utm_to_latlon  # noqa: E402
from area_bounds import latlon_bbox, local_bounds  # noqa: E402  (path added by fetch_lod2)
from fetch_dgm import latlon_to_utm  # noqa: E402
from area_mask import AreaMask, stac_items  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
AREAS_FILE = ROOT / "Tools" / "osm" / "areas.json"
DATA = ROOT / "Data"
STAC = {"dom1": "https://dom.stac.lgln.niedersachsen.de/search", "dgm1": "https://dgm.stac.lgln.niedersachsen.de/search"}

MIN_HEIGHT = 3.5        # m – lower is shrubs, cars, fences
PEAK_WINDOW = 5         # px (= m) neighbourhood for a tree top
MIN_SPACING = 3.0       # m between detected trees
BUILDING_BUFFER = 1.5   # m around LoD2 footprints


def newest_tiles(collection, bbox):
    best = {}
    for f in stac_items(STAC[collection], bbox, collection):
        parts = f["id"].split("_")          # dom1_32_426_5900_1_ni_2021
        key = (int(parts[2]), int(parts[3]))
        year = int(parts[-1])
        if key not in best or year > best[key][0]:
            best[key] = (year, f["assets"][f"{collection}-tif"]["href"])
    return best


def load_raster(href):
    with urllib.request.urlopen(urllib.request.Request(href, headers={"User-Agent": "Nordisch-trees/0.1"}), timeout=300) as r:
        im = Image.open(io.BytesIO(r.read()))
    arr = np.array(im, dtype=np.float32)
    tie, scale = im.tag_v2.get(33922), im.tag_v2.get(33550)
    return arr, float(tie[3]), float(tie[4]), float(scale[0])


def building_rings(area_id):
    """Ground-level footprint rings (local x, z) from the LoD2 export."""
    path = DATA / f"{area_id}.lod2.json"
    if not path.exists():
        print("  ! no LoD2 file – buildings are not masked")
        return []
    rings = []
    for b in json.loads(path.read_text())["buildings"]:
        for s in b["s"]:
            if s["t"] == "G":
                rings.append([(p[0], p[2]) for p in s["r"]])
        if not any(s["t"] == "G" for s in b["s"]):
            pts = [(p[0], p[2]) for s in b["s"] for p in s["r"] if p[1] < 0.5]
            if len(pts) >= 3:
                rings.append(pts)
    return rings


_utm_cache = {}


def rasterize_mask(rings, frame, e0, n0, shape):
    """Boolean building mask in tile pixel space (row = north→south), dilated by BUILDING_BUFFER."""
    from PIL import ImageDraw
    if _utm_cache.get("rings") is not rings:
        # UTM of every footprint once (vectorised), with bounds – large areas have tens of thousands of houses.
        utm = []
        for ring in rings:
            r = np.asarray(ring, dtype=np.float64)
            e, n = latlon_to_utm(frame.lat0 + r[:, 1] / frame.m_lat, frame.lon0 + r[:, 0] / frame.m_lon)
            utm.append((np.stack([e, n], axis=1), e.min(), e.max(), n.min(), n.max()))
        _utm_cache["rings"], _utm_cache["utm"] = rings, utm
    img = Image.new("L", (shape[1], shape[0]), 0)
    draw = ImageDraw.Draw(img)
    for en, emin, emax, nmin, nmax in _utm_cache["utm"]:
        if len(en) < 3 or emax < e0 - 50 or emin > e0 + shape[1] + 50 or nmin > n0 + 50 or nmax < n0 - shape[0] - 50:
            continue
        draw.polygon([(e - e0, n0 - n) for e, n in en], fill=1)
    mask = np.array(img, dtype=bool)
    if BUILDING_BUFFER > 0:
        mask = ndimage.binary_dilation(mask, iterations=int(round(BUILDING_BUFFER)))
    return mask


def crown_radius(ndom, r, c, top):
    """Mean distance (px) until the canopy drops below 55 % of the top, over 8 directions."""
    limit = top * 0.55
    dists = []
    for dr, dc in ((0, 1), (1, 0), (0, -1), (-1, 0), (1, 1), (1, -1), (-1, 1), (-1, -1)):
        step = math.hypot(dr, dc)
        d = 0
        rr, cc = r, c
        while d < 12:
            rr += dr; cc += dc; d += 1
            if not (0 <= rr < ndom.shape[0] and 0 <= cc < ndom.shape[1]) or ndom[rr, cc] < limit:
                break
        dists.append(d * step)
    return float(np.clip(np.mean(dists), 1.0, 9.0))


def process_area(area):
    frame = LocalFrame(area["lat"], area["lon"])
    x0, x1, z0, z1 = local_bounds(area)
    s, w, n, e = latlon_bbox(area, 20)
    dom = newest_tiles("dom1", [w, s, e, n])
    dgm = newest_tiles("dgm1", [w, s, e, n])
    rings = building_rings(area["id"])
    mask = AreaMask.load(area["id"])  # large areas: trees only inside the mask (make_mask.py)
    if mask is not None:
        dom = {k: v for k, v in dom.items() if mask.utm_tile_inside(k, frame, utm_to_latlon)}
    print(f"[{area['id']}] {len(dom)} DOM1 tiles, {len(rings)} building footprints", flush=True)

    trees = []
    for key, (year, href) in sorted(dom.items()):
        if key not in dgm:
            print(f"    {key}: no DGM1 tile, skipped")
            continue
        surf, e0, n0, px = load_raster(href)
        terr, te0, tn0, _ = load_raster(dgm[key][1])
        if surf.shape != terr.shape or abs(e0 - te0) > 0.01 or abs(n0 - tn0) > 0.01:
            print(f"    {key}: DOM/DGM grids differ, skipped")
            continue
        ndom = surf - terr
        ndom[(surf < -100) | (terr < -100)] = 0
        ndom = np.clip(ndom, 0, 60)
        ndom[rasterize_mask(rings, frame, e0, n0, ndom.shape)] = 0
        smooth = ndimage.gaussian_filter(ndom, 1.0)

        peaks = (smooth == ndimage.maximum_filter(smooth, size=PEAK_WINDOW)) & (smooth >= MIN_HEIGHT)
        rows, cols = np.nonzero(peaks)
        kept = 0
        for r, c in zip(rows, cols):
            utm_e = e0 + (c + 0.5) * px
            utm_n = n0 - (r + 0.5) * px
            lat, lon = utm_to_latlon(utm_e, utm_n)
            x, z = frame.to_local(lat, lon)
            if not (x0 <= x <= x1 and z0 <= z <= z1) or (mask is not None and not mask.inside(x, z)):
                continue
            h = float(ndom[r, c])
            trees.append([round(x, 1), round(z, 1), round(h, 1), round(crown_radius(smooth, r, c, h) * px, 1)])
            kept += 1
        print(f"    {key} ({year}): {kept} trees", flush=True)

    # Thin out duplicates closer than MIN_SPACING (keep the taller one).
    trees.sort(key=lambda t: -t[2])
    cell = {}
    result = []
    for t in trees:
        k = (int(t[0] // MIN_SPACING), int(t[1] // MIN_SPACING))
        near = False
        for dx in (-1, 0, 1):
            for dz in (-1, 0, 1):
                for o in cell.get((k[0] + dx, k[1] + dz), []):
                    if (o[0] - t[0]) ** 2 + (o[1] - t[1]) ** 2 < MIN_SPACING ** 2:
                        near = True
                        break
        if not near:
            cell.setdefault(k, []).append(t)
            result.append(t)

    out = DATA / f"{area['id']}.trees.json"
    out.write_text(json.dumps({"source": "LGLN DOM1-DGM1 (CC BY 4.0)", "trees": result}, separators=(",", ":")))
    hs = [t[2] for t in result] or [0]
    print(f"  -> {out.relative_to(ROOT)}: {len(result)} trees, height median {np.median(hs):.1f} m, max {max(hs):.1f} m", flush=True)


def main(argv):
    areas = json.loads(AREAS_FILE.read_text())["areas"]
    wanted = set(argv[1:])
    for a in areas:
        if not wanted or a["id"] in wanted:
            process_area(a)


if __name__ == "__main__":
    main(sys.argv)
