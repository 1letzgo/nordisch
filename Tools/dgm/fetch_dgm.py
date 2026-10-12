#!/usr/bin/env python3
"""Fetch LGLN DGM1 terrain (1 m GeoTIFF tiles) for the game areas and resample it to a 2 m
height grid in the local game frame.

Usage:
    python3 Tools/dgm/fetch_dgm.py            # all areas
    python3 Tools/dgm/fetch_dgm.py langeoog   # single area

Output Data/<id>.dgm.bin (little endian):
    int32  n          grid points per side
    float  extent     metres from centre to grid edge (grid spans -extent..+extent on x and z)
    float  step       metres between samples
    float  base       NHN height subtracted from all samples (area minimum)
    float  h[n*n]     heights, row-major: row = z index (south→north), column = x index (west→east)

Data: Digitales Geländemodell DGM1, © LGLN, CC BY 4.0.
"""
import io
import json
import math
import struct
import sys
import urllib.request
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lod2"))
from fetch_lod2 import LocalFrame  # noqa: E402  (same projection as the importer)

ROOT = Path(__file__).resolve().parents[2]
AREAS_FILE = ROOT / "Tools" / "osm" / "areas.json"
OUT_DIR = ROOT / "Data"
STAC = "https://dgm.stac.lgln.niedersachsen.de/search"
MARGIN = 60.0   # same margin as the ground mesh
STEP = 2.0


def latlon_to_utm(lat, lon, zone=32):
    """Forward transverse Mercator (GRS80)."""
    a = 6378137.0
    f = 1 / 298.257222101
    k0 = 0.9996
    e2 = f * (2 - f)
    ep2 = e2 / (1 - e2)
    phi = math.radians(lat)
    lam = math.radians(lon)
    lam0 = math.radians((zone - 1) * 6 - 180 + 3)
    n = a / math.sqrt(1 - e2 * math.sin(phi) ** 2)
    t = math.tan(phi) ** 2
    c = ep2 * math.cos(phi) ** 2
    A = math.cos(phi) * (lam - lam0)
    m = a * ((1 - e2 / 4 - 3 * e2 ** 2 / 64 - 5 * e2 ** 3 / 256) * phi
             - (3 * e2 / 8 + 3 * e2 ** 2 / 32 + 45 * e2 ** 3 / 1024) * math.sin(2 * phi)
             + (15 * e2 ** 2 / 256 + 45 * e2 ** 3 / 1024) * math.sin(4 * phi)
             - (35 * e2 ** 3 / 3072) * math.sin(6 * phi))
    x = k0 * n * (A + (1 - t + c) * A ** 3 / 6 + (5 - 18 * t + t * t + 72 * c - 58 * ep2) * A ** 5 / 120) + 500000.0
    y = k0 * (m + n * math.tan(phi) * (A * A / 2 + (5 - t + 9 * c + 4 * c * c) * A ** 4 / 24
                                       + (61 - 58 * t + t * t + 600 * c - 330 * ep2) * A ** 6 / 720))
    return x, y


def find_tiles(bbox):
    url = STAC + "?collections=dgm1&limit=100&bbox=" + ",".join(f"{v:.6f}" for v in bbox)
    with urllib.request.urlopen(url, timeout=60) as r:
        feats = json.load(r)["features"]
    # Several survey years per tile – keep the newest.
    best = {}
    for f in feats:
        parts = f["id"].split("_")            # dgm1_32_400_5957_1_ni_2022
        key = (int(parts[2]), int(parts[3]))
        year = int(parts[-1])
        if key not in best or year > best[key][0]:
            best[key] = (year, f["assets"]["dgm1-tif"]["href"])
    return best


def load_tile(href):
    with urllib.request.urlopen(urllib.request.Request(href, headers={"User-Agent": "Nordisch-DGM/0.1"}), timeout=300) as r:
        im = Image.open(io.BytesIO(r.read()))
    arr = np.array(im, dtype=np.float32)
    # GeoTIFF georeference: ModelTiepoint (33922) + ModelPixelScale (33550).
    tie = im.tag_v2.get(33922)
    scale = im.tag_v2.get(33550)
    e0, n0 = float(tie[3]), float(tie[4])
    sx, sy = float(scale[0]), float(scale[1])
    return arr, e0, n0, sx, sy


def process_area(area):
    lat0, lon0, half = area["lat"], area["lon"], area["size_m"] / 2
    frame = LocalFrame(lat0, lon0)
    extent = half + MARGIN
    dlat = (extent + 30) / 111320
    dlon = (extent + 30) / (111320 * math.cos(math.radians(lat0)))
    tiles = find_tiles([lon0 - dlon, lat0 - dlat, lon0 + dlon, lat0 + dlat])
    print(f"[{area['id']}] {len(tiles)} DGM1 tiles", flush=True)
    loaded = {}
    for key, (year, href) in tiles.items():
        print(f"    {key} ({year})", flush=True)
        loaded[key] = load_tile(href)

    n = int(round(2 * extent / STEP)) + 1
    grid = np.zeros((n, n), dtype=np.float32)
    missing = 0
    for iz in range(n):
        z = -extent + iz * STEP
        for ix in range(n):
            x = -extent + ix * STEP
            lat = lat0 + z / frame.m_lat
            lon = lon0 + x / frame.m_lon
            e, nn = latlon_to_utm(lat, lon)
            key = (int(e // 1000), int(nn // 1000))
            tile = loaded.get(key)
            if tile is None:
                missing += 1
                grid[iz, ix] = np.nan
                continue
            arr, e0, n0, sx, sy = tile
            # Pixel centres: column = (e - e0)/sx - 0.5, row = (n0 - n)/sy - 0.5 ; bilinear.
            fc = (e - e0) / sx - 0.5
            fr = (n0 - nn) / sy - 0.5
            c0 = int(math.floor(fc)); r0 = int(math.floor(fr))
            c0 = min(max(c0, 0), arr.shape[1] - 2); r0 = min(max(r0, 0), arr.shape[0] - 2)
            tx = min(max(fc - c0, 0.0), 1.0); ty = min(max(fr - r0, 0.0), 1.0)
            v = (arr[r0, c0] * (1 - tx) * (1 - ty) + arr[r0, c0 + 1] * tx * (1 - ty)
                 + arr[r0 + 1, c0] * (1 - tx) * ty + arr[r0 + 1, c0 + 1] * tx * ty)
            grid[iz, ix] = v
    # Nodata (< -100) and gaps: fill with the median of valid samples.
    grid[grid < -100] = np.nan
    if np.isnan(grid).any():
        grid[np.isnan(grid)] = np.nanmedian(grid)
    base = float(grid.min())
    grid -= base

    out = OUT_DIR / f"{area['id']}.dgm.bin"
    with open(out, "wb") as f:
        f.write(struct.pack("<ifff", n, extent, STEP, base))
        f.write(grid.astype("<f4").tobytes())
    print(f"  -> {out.relative_to(ROOT)}: {n}x{n}, relief {grid.max():.1f} m (base {base:.1f} m NHN), "
          f"{missing} samples outside tiles", flush=True)


def main(argv):
    areas = json.loads(AREAS_FILE.read_text())["areas"]
    wanted = set(argv[1:])
    for a in areas:
        if not wanted or a["id"] in wanted:
            process_area(a)


if __name__ == "__main__":
    main(sys.argv)
