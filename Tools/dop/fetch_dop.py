#!/usr/bin/env python3
"""Fetch LGLN aerial images (DOP20, RGB + near infrared) for a game area, resampled to the local frame.

Usage:
    python3 Tools/dop/fetch_dop.py              # all areas
    python3 Tools/dop/fetch_dop.py westerstede  # single area

Output:
    Data/<id>.dop.png   RGBA, 1 m per pixel, A = near infrared (for NDVI). Top row = north edge.
    Data/<id>.dop.json  {"xMin", "zMin", "step", "width", "height", "date"} – pixel (0,0) of the
                        *bottom* row is the cell [xMin, xMin+step) × [zMin, zMin+step) in local metres.
Covers the area rectangle + 60 m (same margin as the ground mesh and the map textures).

The importer uses it for measured roof colours and ground cover (grass / paving / sand / soil).
Needs rasterio (pip3 install --user rasterio); tiles are cloud-optimised GeoTIFFs, only a
downsampled overview is read.

Data: DOP20 © LGLN, CC BY 4.0.
"""
import json
import sys
import urllib.request
from pathlib import Path

import numpy as np
import rasterio
from rasterio.enums import Resampling
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lod2"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "dgm"))
from fetch_lod2 import LocalFrame  # noqa: E402
from area_bounds import latlon_bbox, local_bounds  # noqa: E402
from fetch_dgm import latlon_to_utm  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
AREAS_FILE = ROOT / "Tools" / "osm" / "areas.json"
DATA = ROOT / "Data"
STAC = "https://dop.stac.lgln.niedersachsen.de/search"
STEP = 1.0      # m per output pixel
MARGIN = 60.0   # m around the area rectangle


def newest_tiles(bbox):
    url = STAC + "?limit=200&bbox=" + ",".join(f"{v:.6f}" for v in bbox)
    with urllib.request.urlopen(url, timeout=60) as r:
        feats = json.load(r)["features"]
    best = {}
    for f in feats:
        parts = f["id"].split("_")          # dop20rgbi_32_427_5901_1_ni_2026-05-25
        key = (int(parts[2]), int(parts[3]))
        date = parts[-1]
        if key not in best or date > best[key][0]:
            best[key] = (date, f["assets"]["dop20_rgbi"]["href"])
    return best


def local_to_utm_affine(frame, x0, x1, z0, z1):
    """Least-squares affine local (x, z) → UTM (e, n); exact enough (< 5 cm) over a few km."""
    xs, zs = np.meshgrid(np.linspace(x0, x1, 6), np.linspace(z0, z1, 6))
    A, E, N = [], [], []
    for x, z in zip(xs.ravel(), zs.ravel()):
        e, n = latlon_to_utm(frame.lat0 + z / frame.m_lat, frame.lon0 + x / frame.m_lon)
        A.append([x, z, 1.0]); E.append(e); N.append(n)
    A = np.array(A)
    ce = np.linalg.lstsq(A, np.array(E), rcond=None)[0]
    cn = np.linalg.lstsq(A, np.array(N), rcond=None)[0]
    return ce, cn


def process_area(area):
    frame = LocalFrame(area["lat"], area["lon"])
    bx0, bx1, bz0, bz1 = local_bounds(area)
    x0, x1, z0, z1 = bx0 - MARGIN, bx1 + MARGIN, bz0 - MARGIN, bz1 + MARGIN
    w, h = int(np.ceil((x1 - x0) / STEP)), int(np.ceil((z1 - z0) / STEP))
    s, wl, n, e = latlon_bbox(area, MARGIN + 20)
    tiles = newest_tiles([wl, s, e, n])
    print(f"[{area['id']}] {len(tiles)} DOP20 tiles, output {w}×{h} px", flush=True)

    ce, cn = local_to_utm_affine(frame, x0, x1, z0, z1)
    # Pixel centres; row 0 = north.
    xs = x0 + (np.arange(w) + 0.5) * STEP
    zs = z1 - (np.arange(h) + 0.5) * STEP
    X, Z = np.meshgrid(xs, zs)
    E = ce[0] * X + ce[1] * Z + ce[2]
    N = cn[0] * X + cn[1] * Z + cn[2]

    out = np.zeros((h, w, 4), dtype=np.uint8)
    dates = set()
    with rasterio.Env(GDAL_HTTP_USERAGENT="Nordisch-dop/0.1", GDAL_DISABLE_READDIR_ON_OPEN="EMPTY_DIR"):
        for key, (date, href) in sorted(tiles.items()):
            with rasterio.open(href) as src:
                b = src.bounds
                inside = (E >= b.left) & (E < b.right) & (N > b.bottom) & (N <= b.top)
                if not inside.any():
                    continue
                tw = int(round((b.right - b.left) / STEP))
                th = int(round((b.top - b.bottom) / STEP))
                img = src.read(out_shape=(src.count, th, tw), resampling=Resampling.average)  # (4, th, tw)
            cols = np.clip(((E[inside] - b.left) / STEP).astype(int), 0, tw - 1)
            rows = np.clip(((b.top - N[inside]) / STEP).astype(int), 0, th - 1)
            for band in range(4):
                out[..., band][inside] = img[band, rows, cols]
            dates.add(date)
            print(f"    {key} ({date}): {int(inside.sum())} px", flush=True)

    png = DATA / f"{area['id']}.dop.png"
    Image.fromarray(out, "RGBA").save(png, optimize=True)
    meta = {"xMin": round(x0, 3), "zMin": round(z0, 3), "step": STEP, "width": w, "height": h,
            "date": max(dates) if dates else None, "source": "LGLN DOP20 RGBI (CC BY 4.0)"}
    (DATA / f"{area['id']}.dop.json").write_text(json.dumps(meta, indent=1))
    print(f"  -> {png.relative_to(ROOT)} ({png.stat().st_size / 1e6:.1f} MB)", flush=True)


def main(argv):
    areas = json.loads(AREAS_FILE.read_text())["areas"]
    wanted = set(argv[1:])
    for a in areas:
        if not wanted or a["id"] in wanted:
            process_area(a)


if __name__ == "__main__":
    main(sys.argv)
