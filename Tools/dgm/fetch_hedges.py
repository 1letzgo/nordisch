#!/usr/bin/env python3
"""Detect hedges for a game area from the LGLN image-based surface model bDOM20 (same flight as the DOP20
aerial image, i.e. current – new estates included), DGM1 terrain and the DOP20 near infrared.

Usage:
    python3 Tools/dgm/fetch_hedges.py              # all areas
    python3 Tools/dgm/fetch_hedges.py westerstede  # single area

Per 1 km tile, 0.4 m raster (overview level of the 20 cm COGs):
    nDOM  = bDOM − DGM1, all built houses masked (+0.4 m)
    green = NDVI from DOP20 RGBI ≥ 0.15
    hedge pixel = 0.6 m ≤ nDOM ≤ 5 m and green

1. Border hedges: every border of a garden plot (ALKIS parcel with an addressed house, 120–2500 m²) is
   sampled every 0.4 m; a sample is "hedge" if any pixel in a ±1.2 m strip across the border is. Gaps ≤ 2 m
   are closed, runs ≥ 2 m kept – the hedge lies exactly on the border, continuous into the corners.
2. Free hedges outside the gardens (along paths, field hedgerows): RANSAC lines through the hedge pixels
   (inliers ≤ 0.9 m, split at gaps > 2.5 m, ≥ 6 m long, ≤ 2.4 m wide); pieces inside garden plots (shrubs,
   crown edges), nursery rows and pieces running along a border hedge are dropped.

Output Data/<id>.hedges.json: {"hedges": [[x0, z0, x1, z1, height, width, kind], ...]} in local metres
(x = east, z = north; kind 1 = on a plot border, 0 = free). Needs Data/<id>.parcels.json and
Assets/Generated/<id>/<id>_buildings.json (run one Unity import first). rasterio required.

Data: bDOM20 / DGM1 / DOP20 © LGLN, CC BY 4.0.
"""
import json
import math
import sys
import urllib.request
from pathlib import Path

import numpy as np
import rasterio
from rasterio.enums import Resampling
from PIL import Image, ImageDraw
from scipy import ndimage

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lod2"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "dop"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from fetch_lod2 import LocalFrame  # noqa: E402
from area_bounds import latlon_bbox, local_bounds  # noqa: E402
from fetch_dop import local_to_utm_affine  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
AREAS_FILE = ROOT / "Tools" / "osm" / "areas.json"
DATA = ROOT / "Data"
STAC = {"bdom": "https://bdom.stac.lgln.niedersachsen.de/search", "dgm1": "https://dgm.stac.lgln.niedersachsen.de/search",
        "dop": "https://dop.stac.lgln.niedersachsen.de/search"}

RES = 0.4                       # m per pixel (2500 px per 1 km tile)
MIN_H, MAX_H, NDVI_MIN = 0.6, 5.0, 0.15
STRIP, SAMPLE = 1.2, 0.4        # border: half strip width, sample spacing
CLOSE_GAP, MIN_BORDER_RUN = 2.0, 2.0
INLIER, GAP, BAND, MIN_LEN, MAX_WIDTH = 0.9, 2.5, 1.8, 6.0, 2.4   # free hedges: outside gardens only


# ------------------------------------------------------------------ tiles

def tiles(kind, bbox):
    url = STAC[kind] + ("?collections=dgm1&" if kind == "dgm1" else "?") + "limit=200&bbox=" + ",".join(f"{v:.6f}" for v in bbox)
    with urllib.request.urlopen(url, timeout=60) as r:
        feats = json.load(r)["features"]
    best = {}
    for f in feats:
        p = f["id"].split("_")              # bdom20_32_426_5900_1_ni_2026-05-25 / dgm1_32_426_5900_1_ni_2021
        key, date = (int(p[2]), int(p[3])), p[-1]
        assets = f["assets"]
        pref = [k for k in assets if "rgbi" in k] + list(assets)   # DOP: the RGB + near-infrared file
        href = next((assets[k]["href"] for k in pref if assets[k].get("href", "").endswith(".tif")), None)
        if href and (key not in best or date > best[key][0]):
            best[key] = (date, href)
    return best


def read(href, bands=1):
    n = int(round(1000 / RES))
    with rasterio.open(href) as src:
        b = src.bounds
        arr = src.read(list(range(1, bands + 1)), out_shape=(bands, n, n), resampling=Resampling.average)
    return arr.astype(np.float32), b.left, b.top


# ------------------------------------------------------------------ plots / buildings

def ring_area(r):
    return abs(sum(r[i][0] * r[(i + 1) % len(r)][1] - r[(i + 1) % len(r)][0] * r[i][1] for i in range(len(r)))) / 2


def point_in(p, r):
    x, y = p
    inside = False
    j = len(r) - 1
    for i in range(len(r)):
        xi, yi = r[i]; xj, yj = r[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
            inside = not inside
        j = i
    return inside


def load_plots(area_id):
    """Garden plots (rings, local) and all building footprints (from the importer's catalog)."""
    cat = json.loads((ROOT / "Assets" / "Generated" / area_id / f"{area_id}_buildings.json").read_text())["buildings"]
    houses = [b["ring"] for b in cat]
    mains = [(sum(p[0] for p in b["ring"]) / len(b["ring"]), sum(p[1] for p in b["ring"]) / len(b["ring"])) for b in cat if b["addr"]]
    parcels = json.loads((DATA / f"{area_id}.parcels.json").read_text())["parcels"]
    grid = {}
    for c in mains:
        grid.setdefault((int(c[0] // 50), int(c[1] // 50)), []).append(c)
    plots = []
    for p in parcels:
        r = p["ring"]
        if not 120 <= ring_area(r) <= 2500:
            continue
        xs = [q[0] for q in r]; zs = [q[1] for q in r]
        found = any(point_in(c, r)
                    for gx in range(int(min(xs) // 50), int(max(xs) // 50) + 1)
                    for gz in range(int(min(zs) // 50), int(max(zs) // 50) + 1)
                    for c in grid.get((gx, gz), []))
        if found:
            plots.append(r)
    return plots, houses


def border_edges(plots):
    """Unique plot border segments (shared borders once)."""
    seen, edges = set(), []
    for r in plots:
        for i in range(len(r)):
            a, b = tuple(r[i]), tuple(r[(i + 1) % len(r)])
            if math.dist(a, b) < 0.8:
                continue
            k = tuple(sorted([(round(a[0], 1), round(a[1], 1)), (round(b[0], 1), round(b[1], 1))]))
            if k in seen:
                continue
            seen.add(k)
            edges.append((a, b))
    return edges


# ------------------------------------------------------------------ free hedges

def ransac_lines(pts, rng):
    """Greedy RANSAC: [(p0, p1, member_idx, width)]; points within BAND of an accepted run belong to it."""
    out = []
    remaining = np.arange(len(pts))
    while len(remaining) >= 12:
        P = pts[remaining]
        best = None
        for _ in range(80):
            i, j = rng.choice(len(P), 2, replace=False)
            d = P[j] - P[i]
            L = math.hypot(*d)
            if L < 2.0:
                continue
            u = d / L
            nrm = np.array([-u[1], u[0]])
            inl = np.nonzero(np.abs((P - P[i]) @ nrm) <= INLIER)[0]
            if best is None or len(inl) > len(best[1]):
                best = (u, inl, P[i])
        if best is None or len(best[1]) < 12:
            break
        u, inl, origin = best
        nrm = np.array([-u[1], u[0]])
        t = (P[inl] - origin) @ u
        order = np.argsort(t); t, inl = t[order], inl[order]
        tall, dall = (P - origin) @ u, (P - origin) @ nrm
        taken = np.zeros(len(P), dtype=bool)
        start = 0
        for k in range(1, len(t) + 1):
            if k == len(t) or t[k] - t[k - 1] > GAP:
                ta, tb = t[start], t[k - 1]
                band = (np.abs(dall) <= BAND) & (tall >= ta - 0.5) & (tall <= tb + 0.5)
                taken |= band
                if tb - ta >= MIN_LEN:
                    off = dall[band]
                    width = float(np.percentile(off, 95) - np.percentile(off, 5)) + RES
                    out.append((origin + u * ta, origin + u * tb, remaining[np.nonzero(band)[0]], width))
                start = k
        taken[inl] = True
        remaining = remaining[~taken]
    return out


def in_plot(f, plots, _cache={}):
    """Free pieces inside a garden plot are shrubs/crown edges – the plot's hedges are its border hedges."""
    key = id(plots)
    if key not in _cache:
        grid = {}
        for i, r in enumerate(plots):
            xs = [q[0] for q in r]; zs = [q[1] for q in r]
            for gx in range(int(min(xs) // 30), int(max(xs) // 30) + 1):
                for gz in range(int(min(zs) // 30), int(max(zs) // 30) + 1):
                    grid.setdefault((gx, gz), []).append(i)
        _cache.clear(); _cache[key] = grid
    m = ((f[0] + f[2]) / 2, (f[1] + f[3]) / 2)
    return any(point_in(m, plots[i]) for i in _cache[key].get((int(m[0] // 30), int(m[1] // 30)), []))


def drop_rows(hedges, neighbours=3):
    """Pieces with ≥ 3 parallel neighbours within 8 m are nursery / crop rows."""
    if not hedges:
        return hedges
    mids = np.array([[(h[0] + h[2]) / 2, (h[1] + h[3]) / 2] for h in hedges])
    dirs = np.array([[h[2] - h[0], h[3] - h[1]] for h in hedges])
    dirs = dirs / np.maximum(np.linalg.norm(dirs, axis=1, keepdims=True), 1e-6)
    cell = {}
    for i, m in enumerate(mids):
        cell.setdefault((int(m[0] // 8), int(m[1] // 8)), []).append(i)
    keep = []
    for i, m in enumerate(mids):
        k = (int(m[0] // 8), int(m[1] // 8)); n = 0
        for dx in (-1, 0, 1):
            for dz in (-1, 0, 1):
                for j in cell.get((k[0] + dx, k[1] + dz), []):
                    if j != i and np.linalg.norm(mids[j] - m) < 8 and abs(dirs[j] @ dirs[i]) > 0.96:
                        n += 1
        if n < neighbours:
            keep.append(hedges[i])
    return keep


# ------------------------------------------------------------------ main

def process_area(area):
    aid = area["id"]
    frame = LocalFrame(area["lat"], area["lon"])
    x0, x1, z0, z1 = local_bounds(area)
    s, w, n, e = latlon_bbox(area, 20)
    bdom, dgm, dop = tiles("bdom", [w, s, e, n]), tiles("dgm1", [w, s, e, n]), tiles("dop", [w, s, e, n])
    plots, houses = load_plots(aid)
    edges = border_edges(plots)
    date = max(d for d, _ in bdom.values()) if bdom else "?"
    print(f"[{aid}] {len(bdom)} bDOM tiles ({date}), {len(plots)} garden plots, {len(edges)} borders", flush=True)

    # local <-> UTM (affine, exact to cm over a few km)
    ce, cn = local_to_utm_affine(frame, x0 - 50, x1 + 50, z0 - 50, z1 + 50)
    M = np.array([[ce[0], ce[1]], [cn[0], cn[1]]]); Minv = np.linalg.inv(M); off = np.array([ce[2], cn[2]])
    to_utm = lambda xz: np.asarray(xz) @ M.T + off
    to_local = lambda en: (np.asarray(en) - off) @ Minv.T

    # Border samples: positions along every edge, each with a strip of sub-samples across the border.
    across = np.arange(-STRIP, STRIP + 1e-6, RES)
    samples, sub = [], []
    for ei, (a, b) in enumerate(edges):
        a, b = np.array(a), np.array(b)
        L = float(np.linalg.norm(b - a)); u = (b - a) / L; nrm = np.array([-u[1], u[0]])
        for t in np.arange(SAMPLE / 2, L, SAMPLE):
            samples.append((ei, t))
            sub.append(a + u * t + np.outer(across, nrm))
    sub = np.concatenate(sub) if sub else np.zeros((0, 2))
    sub_en = to_utm(sub)
    sub_hit = np.zeros(len(sub), dtype=bool)
    sub_h = np.zeros(len(sub), dtype=np.float32)

    free, rng = [], np.random.default_rng(7)
    house_utm = [to_utm(np.array(r)) for r in houses]
    for key, (bdate, href) in sorted(bdom.items()):
        if key not in dgm or key not in dop:
            print(f"    {key}: no DGM1/DOP tile, skipped")
            continue
        surf, e0, n0 = read(href)
        terr, _, _ = read(dgm[key][1])             # 1 m, resampled by rasterio onto the same 0.4 m grid
        rgbi, _, _ = read(dop[key][1], 4)
        surf, terr = surf[0], terr[0]
        ndom = np.where(np.isfinite(surf) & (terr > -100), surf - terr, 0)
        red, nir = rgbi[0], rgbi[3]
        ndvi = (nir - red) / np.maximum(nir + red, 1)
        img = Image.new("L", ndom.shape[::-1], 0); dr = ImageDraw.Draw(img)
        for hu in house_utm:
            if hu[:, 0].max() < e0 or hu[:, 0].min() > e0 + 1000 or hu[:, 1].max() < n0 - 1000 or hu[:, 1].min() > n0:
                continue
            dr.polygon([((p[0] - e0) / RES, (n0 - p[1]) / RES) for p in hu], fill=1)
        bmask = ndimage.binary_dilation(np.array(img, dtype=bool), iterations=1)
        hedge = (ndom >= MIN_H) & (ndom <= MAX_H) & (ndvi >= NDVI_MIN) & ~bmask

        # 1. border strips
        c = np.floor((sub_en[:, 0] - e0) / RES).astype(int); r = np.floor((n0 - sub_en[:, 1]) / RES).astype(int)
        ok = (c >= 0) & (c < hedge.shape[1]) & (r >= 0) & (r < hedge.shape[0])
        idx = np.nonzero(ok)[0]
        sub_hit[idx] = hedge[r[idx], c[idx]]
        sub_h[idx] = np.where(sub_hit[idx], ndom[r[idx], c[idx]], 0)

        # 2. free hedges
        labels, count = ndimage.label(hedge, structure=np.ones((3, 3)))
        found = 0
        for sl, li in zip(ndimage.find_objects(labels), range(1, count + 1)):
            if sl is None:
                continue
            m = labels[sl] == li
            npx = int(m.sum())
            if npx * RES * RES < 4 or (npx * RES * RES > 60 and npx / m.size > 0.5):
                continue  # specks / compact shrubbery
            rr, cc = np.nonzero(m)
            rr = rr + sl[0].start; cc = cc + sl[1].start
            en = np.stack([e0 + (cc + 0.5) * RES, n0 - (rr + 0.5) * RES], axis=1)
            for p0, p1, members, width in ransac_lines(en, rng):
                if width > MAX_WIDTH:
                    continue
                h = float(np.percentile(ndom[rr[members], cc[members]], 75))
                la, lb = to_local(np.array([p0, p1]))
                free.append([la[0], la[1], lb[0], lb[1], h, float(np.clip(width - RES, 0.6, 2.0))])
                found += 1
        print(f"    {key}: bDOM {bdate}, {int(hedge.sum() * RES * RES)} m² hedge pixels, {found} free pieces", flush=True)

    # Border runs per edge (gaps ≤ CLOSE_GAP closed).
    k = len(across)
    hits = sub_hit.reshape(-1, k) if len(sub_hit) else np.zeros((0, k), dtype=bool)
    hit, hmax, wpx = hits.any(axis=1), sub_h.reshape(-1, k).max(axis=1) if len(sub_h) else np.zeros(0), hits.sum(axis=1)
    border = []
    i = 0
    while i < len(samples):
        ei = samples[i][0]
        j = i
        while j < len(samples) and samples[j][0] == ei:
            j += 1
        ts = np.array([samples[q][1] for q in range(i, j)])
        hs, hh, wp = hit[i:j].copy(), hmax[i:j], wpx[i:j]
        q = 0
        while q < len(hs):
            if hs[q]:
                q += 1; continue
            g = q
            while g < len(hs) and not hs[g]:
                g += 1
            if q > 0 and g < len(hs) and (g - q) * SAMPLE <= CLOSE_GAP:
                hs[q:g] = True
            q = g
        a, b = np.array(edges[ei][0]), np.array(edges[ei][1])
        L = float(np.linalg.norm(b - a)); u = (b - a) / L
        q = 0
        while q < len(hs):
            if not hs[q]:
                q += 1; continue
            g = q
            while g < len(hs) and hs[g]:
                g += 1
            ta, tb = max(0.0, ts[q] - SAMPLE / 2), min(L, ts[g - 1] + SAMPLE / 2)
            if tb - ta >= MIN_BORDER_RUN:
                vals = hh[q:g][hh[q:g] > 0]
                h = float(np.percentile(vals, 80)) if len(vals) else 1.2
                wv = wp[q:g][wp[q:g] > 0]
                width = float(np.clip(np.median(wv) * RES, 0.6, 2.0)) if len(wv) else 0.8
                pa, pb = a + u * ta, a + u * tb
                border.append([pa[0], pa[1], pb[0], pb[1], h, width])
            q = g
        i = j

    # Free pieces running along a border hedge are that hedge: drop them; nursery rows too.
    bgrid = {}
    for bi, bh in enumerate(border):
        for gx in range(int(min(bh[0], bh[2]) // 20) - 1, int(max(bh[0], bh[2]) // 20) + 2):
            for gz in range(int(min(bh[1], bh[3]) // 20) - 1, int(max(bh[1], bh[3]) // 20) + 2):
                bgrid.setdefault((gx, gz), []).append(bi)

    def along_border(f):
        m = np.array([(f[0] + f[2]) / 2, (f[1] + f[3]) / 2]); d = np.array([f[2] - f[0], f[3] - f[1]]); d /= max(np.linalg.norm(d), 1e-6)
        for bi in bgrid.get((int(m[0] // 20), int(m[1] // 20)), []):
            bh = border[bi]
            a, b = np.array(bh[:2]), np.array(bh[2:4]); ab = b - a; L2 = ab @ ab
            if L2 < 1e-4 or abs((ab / math.sqrt(L2)) @ d) < 0.85:
                continue
            t = np.clip((m - a) @ ab / L2, 0, 1)
            if np.linalg.norm(m - (a + ab * t)) < 2.0:
                return True
        return False

    free = [f for f in drop_rows(free) if not along_border(f) and not in_plot(f, plots)]
    out_list = [[round(float(v), 2) for v in h] + [1] for h in border] + [[round(float(v), 2) for v in h] + [0] for h in free]
    out_list = [h for h in out_list if x0 <= (h[0] + h[2]) / 2 <= x1 and z0 <= (h[1] + h[3]) / 2 <= z1]
    out = DATA / f"{aid}.hedges.json"
    out.write_text(json.dumps({"source": "LGLN bDOM20 + DGM1 + DOP20 (CC BY 4.0)", "date": date, "hedges": out_list},
                              separators=(",", ":")))
    lb = sum(math.hypot(h[2] - h[0], h[3] - h[1]) for h in out_list if h[6] == 1)
    lf = sum(math.hypot(h[2] - h[0], h[3] - h[1]) for h in out_list if h[6] == 0)
    print(f"  -> {out.relative_to(ROOT)}: border {lb / 1000:.1f} km, free {lf / 1000:.1f} km", flush=True)


def main(argv):
    areas = json.loads(AREAS_FILE.read_text())["areas"]
    wanted = set(argv[1:])
    with rasterio.Env(GDAL_HTTP_USERAGENT="Nordisch-hedges/0.2", GDAL_DISABLE_READDIR_ON_OPEN="EMPTY_DIR"):
        for a in areas:
            if not wanted or a["id"] in wanted:
                process_area(a)


if __name__ == "__main__":
    main(sys.argv)
