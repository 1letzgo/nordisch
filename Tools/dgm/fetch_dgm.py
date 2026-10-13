#!/usr/bin/env python3
"""Fetch LGLN DGM1 terrain (1 m GeoTIFF tiles) for the game areas and resample it to a 2 m
height grid in the local game frame.

Usage:
    python3 Tools/dgm/fetch_dgm.py            # all areas
    python3 Tools/dgm/fetch_dgm.py langeoog   # single area

Output Data/<id>.dgm.bin (little endian):
    char[4] "DGM2"
    int32  nx, nz     grid points along x (west→east) and z (south→north)
    float  xMin, zMin local position of sample [0, 0]
    float  step       metres between samples
    float  base       NHN height subtracted from all samples (area minimum)
    float  h[nx*nz]   heights, row-major: row = z index, column = x index
Grids with more than 4 M samples (whole municipalities, "dgm_step": 5 in areas.json) are written gzip-compressed
with magic "DGMQ" and uint16 centimetres instead of floats (same header).

Data: Digitales Geländemodell DGM1, © LGLN, CC BY 4.0.
"""
import gzip
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
from area_bounds import latlon_bbox, local_bounds  # noqa: E402
from area_mask import stac_items  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
AREAS_FILE = ROOT / "Tools" / "osm" / "areas.json"
OUT_DIR = ROOT / "Data"
STAC = "https://dgm.stac.lgln.niedersachsen.de/search"
MARGIN = 60.0   # same margin as the ground mesh
STEP = 2.0      # default; an area may set "dgm_step" (5 = terrain cell size, for very large areas)
COMPACT_SAMPLES = 4_000_000


def latlon_to_utm(lat, lon, zone=32):
    """Forward transverse Mercator (GRS80). Works on scalars and numpy arrays."""
    a = 6378137.0
    f = 1 / 298.257222101
    k0 = 0.9996
    e2 = f * (2 - f)
    ep2 = e2 / (1 - e2)
    phi = np.radians(lat)
    lam = np.radians(lon)
    lam0 = math.radians((zone - 1) * 6 - 180 + 3)
    n = a / np.sqrt(1 - e2 * np.sin(phi) ** 2)
    t = np.tan(phi) ** 2
    c = ep2 * np.cos(phi) ** 2
    A = np.cos(phi) * (lam - lam0)
    m = a * ((1 - e2 / 4 - 3 * e2 ** 2 / 64 - 5 * e2 ** 3 / 256) * phi
             - (3 * e2 / 8 + 3 * e2 ** 2 / 32 + 45 * e2 ** 3 / 1024) * np.sin(2 * phi)
             + (15 * e2 ** 2 / 256 + 45 * e2 ** 3 / 1024) * np.sin(4 * phi)
             - (35 * e2 ** 3 / 3072) * np.sin(6 * phi))
    x = k0 * n * (A + (1 - t + c) * A ** 3 / 6 + (5 - 18 * t + t * t + 72 * c - 58 * ep2) * A ** 5 / 120) + 500000.0
    y = k0 * (m + n * np.tan(phi) * (A * A / 2 + (5 - t + 9 * c + 4 * c * c) * A ** 4 / 24
                                     + (61 - 58 * t + t * t + 600 * c - 330 * ep2) * A ** 6 / 720))
    if np.ndim(x) == 0:
        return float(x), float(y)
    return x, y


def find_tiles(bbox):
    # Several survey years per tile – keep the newest. Large areas have hundreds of tiles (paged).
    best = {}
    for f in stac_items(STAC, bbox, "dgm1"):
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
    lat0, lon0 = area["lat"], area["lon"]
    frame = LocalFrame(lat0, lon0)
    step = float(area.get("dgm_step", STEP))
    bx0, bx1, bz0, bz1 = local_bounds(area)
    # Grid aligned to multiples of the step (5 m = the terrain cell corners of the importer).
    gx0, gz0 = math.floor((bx0 - MARGIN) / step) * step, math.floor((bz0 - MARGIN) / step) * step
    nx = int(math.ceil((bx1 + MARGIN - gx0) / step)) + 1
    nz = int(math.ceil((bz1 + MARGIN - gz0) / step)) + 1
    s, w, nn_, e = latlon_bbox(area, MARGIN + 30)
    tiles = find_tiles([w, s, e, nn_])
    print(f"[{area['id']}] {len(tiles)} DGM1 tiles, grid {nx}x{nz} at {step:g} m", flush=True)

    # UTM position of every sample (exact projection, vectorised), grouped by 1 km tile.
    X, Z = np.meshgrid(gx0 + np.arange(nx) * step, gz0 + np.arange(nz) * step)
    E, N = latlon_to_utm(lat0 + Z.ravel() / frame.m_lat, lon0 + X.ravel() / frame.m_lon)
    del X, Z
    keys = (np.floor(E / 1000).astype(np.int64) << 20) | np.floor(N / 1000).astype(np.int64)
    order = np.argsort(keys, kind="stable")
    uniq, starts = np.unique(keys[order], return_index=True)
    grid = np.full(nx * nz, np.nan, dtype=np.float32)
    missing = 0
    for ui, k in enumerate(uniq):
        idx = order[starts[ui]:(starts[ui + 1] if ui + 1 < len(starts) else len(order))]
        key = (int(k >> 20), int(k & ((1 << 20) - 1)))
        tile = tiles.get(key)
        if tile is None:
            missing += len(idx)
            continue
        print(f"    {key} ({tile[0]})", flush=True)
        arr, e0, n0, sx, sy = load_tile(tile[1])
        # Pixel centres: column = (e - e0)/sx - 0.5, row = (n0 - n)/sy - 0.5 ; bilinear.
        fc = (E[idx] - e0) / sx - 0.5
        fr = (n0 - N[idx]) / sy - 0.5
        c0 = np.clip(np.floor(fc).astype(np.int64), 0, arr.shape[1] - 2)
        r0 = np.clip(np.floor(fr).astype(np.int64), 0, arr.shape[0] - 2)
        tx = np.clip(fc - c0, 0.0, 1.0); ty = np.clip(fr - r0, 0.0, 1.0)
        grid[idx] = (arr[r0, c0] * (1 - tx) * (1 - ty) + arr[r0, c0 + 1] * tx * (1 - ty)
                     + arr[r0 + 1, c0] * (1 - tx) * ty + arr[r0 + 1, c0 + 1] * tx * ty)
    grid = grid.reshape(nz, nx)
    # Nodata (-9999) and gaps: fill with the median of valid samples. Interpolation next to a nodata pixel yields
    # values in between (e.g. -72 m at the coast) – nothing in Lower Saxony lies below -30 m NHN, so drop those too.
    grid[grid < -30] = np.nan
    if np.isnan(grid).any():
        grid[np.isnan(grid)] = np.nanmedian(grid)
    base = float(grid.min())
    grid -= base

    out = OUT_DIR / f"{area['id']}.dgm.bin"
    if nx * nz > COMPACT_SAMPLES:
        # Big areas: centimetres as uint16, gzip-compressed (TerrainField reads both formats).
        with gzip.open(out, "wb", compresslevel=6) as f:
            f.write(b"DGMQ")
            f.write(struct.pack("<iiffff", nx, nz, gx0, gz0, step, base))
            f.write(np.clip(np.round(grid * 100), 0, 65535).astype("<u2").tobytes())
    else:
        with open(out, "wb") as f:
            f.write(b"DGM2")
            f.write(struct.pack("<iiffff", nx, nz, gx0, gz0, step, base))
            f.write(grid.astype("<f4").tobytes())
    print(f"  -> {out.relative_to(ROOT)}: {nx}x{nz}, relief {grid.max():.1f} m (base {base:.1f} m NHN), "
          f"{missing} samples outside tiles, {out.stat().st_size / 1e6:.1f} MB", flush=True)


def main(argv):
    areas = json.loads(AREAS_FILE.read_text())["areas"]
    wanted = set(argv[1:])
    for a in areas:
        if not wanted or a["id"] in wanted:
            process_area(a)


if __name__ == "__main__":
    main(sys.argv)
