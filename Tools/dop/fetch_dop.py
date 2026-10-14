#!/usr/bin/env python3
"""Fetch LGLN aerial images (DOP20, RGB + near infrared) for a game area, resampled to the local frame.

Usage:
    python3 Tools/dop/fetch_dop.py              # all areas
    python3 Tools/dop/fetch_dop.py westerstede  # single area
    python3 Tools/dop/fetch_dop.py westerstede --reuse  # grown area: keep the pixels of the existing image (where
                                                        # it has full resolution or blocks suffice), fetch the rest

Output:
    Data/<id>.dop.png   RGBA, 1 m per pixel ("dop_step" in areas.json for very large areas, e.g. 2), A = near infrared (for NDVI). Top row = north edge.
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
from fetch_lod2 import utm_to_latlon  # noqa: E402
from area_mask import FINE, AreaMask, stac_items  # noqa: E402
from scipy import ndimage  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
AREAS_FILE = ROOT / "Tools" / "osm" / "areas.json"
DATA = ROOT / "Data"
STAC = "https://dop.stac.lgln.niedersachsen.de/search"
STEP = 1.0      # m per output pixel
MARGIN = 60.0   # m around the area rectangle


def newest_tiles(bbox):
    best = {}
    for f in stac_items(STAC, bbox):
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


def read_previous(area, x0, z1, step, w, h, keep):
    """Pixels of the existing image on the same grid and where they are good enough: full resolution there
    (the old mask's detailed part) or only blocks needed now (outside the new detailed part)."""
    png, js = DATA / f"{area['id']}.dop.png", DATA / f"{area['id']}.dop.json"
    if not png.exists() or not js.exists():
        return None, None
    om = json.loads(js.read_text())
    oz1 = om["zMin"] + om["height"] * om["step"]
    dc, dr = (om["xMin"] - x0) / step, (z1 - oz1) / step
    if om["step"] != step or dc != int(dc) or dr != int(dr) or dc < 0 or dr < 0:
        print(f"  ! {png.name}: different grid, not reused", flush=True)
        return None, None
    Image.MAX_IMAGE_PIXELS = None
    old = np.array(Image.open(png))
    dc, dr = int(dc), int(dr)
    hh, ww = min(old.shape[0], h - dr), min(old.shape[1], w - dc)
    out = np.zeros((h, w, 4), dtype=np.uint8)
    out[dr:dr + hh, dc:dc + ww] = old[:hh, :ww]
    have = np.zeros((h, w), dtype=bool)
    have[dr:dr + hh, dc:dc + ww] = True
    old_mask = AreaMask.load(area["id"] + ".old", level=128) if (DATA / f"{area['id']}.old.mask.png").exists() else None
    if old_mask is not None:
        # Old file: full resolution only where its own mask (dilated like below) was inside.
        X = x0 + (np.arange(w) + 0.5) * step
        full = np.zeros((h, w), dtype=bool)
        for r in range(0, h, 512):
            Zr = z1 - (np.arange(r, min(r + 512, h)) + 0.5) * step
            full[r:r + 512] = old_mask.inside(X[None, :], Zr[:, None])
        full = ndimage.binary_dilation(full, iterations=max(1, int(40 / step)))
        have &= full | ~keep
    print(f"  reusing {have.mean() * 100:.0f} % of the pixels from {png.name}", flush=True)
    return out, have


def keep_mask(mask, x0, z1, step, w, h):
    """Pixels kept at full resolution: the mask's inside, grown by 40 m."""
    X = x0 + (np.arange(w) + 0.5) * step
    keep = np.zeros((h, w), dtype=bool)
    for r in range(0, h, 512):
        Zr = z1 - (np.arange(r, min(r + 512, h)) + 0.5) * step
        keep[r:r + 512] = mask.inside(X[None, :], Zr[:, None])
    return ndimage.binary_dilation(keep, iterations=max(1, int(40 / step)))


def process_area(area, reuse=False):
    frame = LocalFrame(area["lat"], area["lon"])
    step = float(area.get("dop_step", STEP))
    bx0, bx1, bz0, bz1 = local_bounds(area)
    x0, x1, z0, z1 = bx0 - MARGIN, bx1 + MARGIN, bz0 - MARGIN, bz1 + MARGIN
    w, h = int(np.ceil((x1 - x0) / step)), int(np.ceil((z1 - z0) / step))
    s, wl, n, e = latlon_bbox(area, MARGIN + 20)
    tiles = newest_tiles([wl, s, e, n])
    print(f"[{area['id']}] {len(tiles)} DOP20 tiles, output {w}×{h} px at {step:g} m", flush=True)

    out = np.zeros((h, w, 4), dtype=np.uint8)
    have = None
    mask = AreaMask.load(area["id"], level=FINE)
    keep = keep_mask(mask, x0, z1, step, w, h) if mask is not None else np.ones((h, w), dtype=bool)
    if reuse:
        prev, have = read_previous(area, x0, z1, step, w, h, keep)
        if prev is not None:
            out = prev
    dates = set()
    with rasterio.Env(GDAL_HTTP_USERAGENT="Nordisch-dop/0.1", GDAL_DISABLE_READDIR_ON_OPEN="EMPTY_DIR"):
        for key, (date, href) in sorted(tiles.items()):
            # Output window covering this 1 km tile (corners via the exact projection, +2 px), pixel centres → UTM.
            xs, zs = [], []
            for ce_, cn_ in ((0, 0), (1, 0), (0, 1), (1, 1)):
                la, lo = utm_to_latlon((key[0] + ce_) * 1000.0, (key[1] + cn_) * 1000.0)
                x, z = frame.to_local(la, lo)
                xs.append(x); zs.append(z)
            c0 = max(int((min(xs) - x0) / step) - 2, 0); c1 = min(int((max(xs) - x0) / step) + 3, w)
            r0 = max(int((z1 - max(zs)) / step) - 2, 0); r1 = min(int((z1 - min(zs)) / step) + 3, h)
            if c0 >= c1 or r0 >= r1:
                continue
            if have is not None and have[r0:r1, c0:c1].all():
                continue  # --reuse: the previous image covers this tile well enough
            X, Z = np.meshgrid(x0 + (np.arange(c0, c1) + 0.5) * step, z1 - (np.arange(r0, r1) + 0.5) * step)
            E, N = latlon_to_utm(frame.lat0 + Z / frame.m_lat, frame.lon0 + X / frame.m_lon)
            with rasterio.open(href) as src:
                b = src.bounds
                inside = (E >= b.left) & (E < b.right) & (N > b.bottom) & (N <= b.top)
                if have is not None:
                    inside &= ~have[r0:r1, c0:c1]  # --reuse: pixels the previous image already has
                if not inside.any():
                    continue
                tw = int(round((b.right - b.left) / step))
                th = int(round((b.top - b.bottom) / step))
                img = src.read(out_shape=(src.count, th, tw), resampling=Resampling.average)  # (4, th, tw)
            cols = np.clip(((E[inside] - b.left) / step).astype(int), 0, tw - 1)
            rows = np.clip(((b.top - N[inside]) / step).astype(int), 0, th - 1)
            win = out[r0:r1, c0:c1]
            for band in range(4):
                win[..., band][inside] = img[band, rows, cols]
            dates.add(date)
            print(f"    {key} ({date}): {int(inside.sum())} px", flush=True)

    if mask is not None:
        # Outside the detailed terrain only field-scale colour matters (coarse terrain, also in the landscape band
        # along a motorway): 16 m block means compress far better.
        b = max(1, int(round(16 / step)))
        hb, wb = h // b * b, w // b * b
        blocks = out[:hb, :wb].reshape(hb // b, b, wb // b, b, 4).mean(axis=(1, 3)).astype(np.uint8)
        coarse = np.repeat(np.repeat(blocks, b, axis=0), b, axis=1)
        sel = ~keep[:hb, :wb]
        out[:hb, :wb][sel] = coarse[sel]
        print(f"  outside the mask: {sel.mean() * 100:.0f} % of the pixels as {b * step:g} m blocks", flush=True)

    png = DATA / f"{area['id']}.dop.png"
    Image.fromarray(out, "RGBA").save(png, optimize=w * h < 40_000_000, compress_level=9)
    meta = {"xMin": round(x0, 3), "zMin": round(z0, 3), "step": step, "width": w, "height": h,
            "date": max(dates) if dates else None, "source": "LGLN DOP20 RGBI (CC BY 4.0)"}
    (DATA / f"{area['id']}.dop.json").write_text(json.dumps(meta, indent=1))
    print(f"  -> {png.relative_to(ROOT)} ({png.stat().st_size / 1e6:.1f} MB)", flush=True)


def main(argv):
    areas = json.loads(AREAS_FILE.read_text())["areas"]
    wanted = {x for x in argv[1:] if not x.startswith("--")}
    for a in areas:
        if not wanted or a["id"] in wanted:
            process_area(a, "--reuse" in argv)


if __name__ == "__main__":
    main(sys.argv)
