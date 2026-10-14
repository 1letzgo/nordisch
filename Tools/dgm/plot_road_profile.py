#!/usr/bin/env python3
"""Plot a motorway height profile before/after the importer's road smoothing (RoadProfile.cs).

Reads Builds/RoadProfile/<id>_roads.csv (written by every import) and draws the longest A28 chain
(or --chain N / --near x,z): full length on top, a 600 m window around its worst pothole below. Needs Pillow + numpy.

    python3 Tools/dgm/plot_road_profile.py westerstede  ->  Builds/RoadProfile/westerstede_A28.png
"""
import argparse
import csv
import os
from collections import defaultdict

import numpy as np
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def load(path):
    chains = defaultdict(lambda: {"ref": "", "group": "", "s": [], "x": [], "z": [], "b": [], "a": []})
    with open(path) as f:
        for row in csv.DictReader(f):
            c = chains[int(row["chain"])]
            c["ref"] = row["ref"]
            c["group"] = row["group"]
            for k, src in (("s", "s"), ("x", "x"), ("z", "z"), ("b", "before"), ("a", "after")):
                c[k].append(float(row[src]))
    return {k: {kk: (np.array(vv) if kk not in ("ref", "group") else vv) for kk, vv in v.items()} for k, v in chains.items()}


def chord_dev(s, h, chord=5.0):
    """h(s) minus the mean of h(s-5), h(s+5)."""
    lo, hi = np.interp(s - chord, s, h), np.interp(s + chord, s, h)
    d = h - 0.5 * (lo + hi)
    d[(s < chord) | (s > s[-1] - chord)] = 0.0
    return d


def panel(draw, box, s, series, title, s_range=None):
    x0, y0, x1, y1 = box
    draw.rectangle(box, outline=(180, 180, 180), fill=(255, 255, 255))
    draw.text((x0 + 8, y0 + 6), title, fill=(30, 30, 30))
    m = np.ones_like(s, dtype=bool) if s_range is None else (s >= s_range[0]) & (s <= s_range[1])
    ss = s[m]
    lo = min(v[m].min() for v, _ in series)
    hi = max(v[m].max() for v, _ in series)
    pad = max(0.2, (hi - lo) * 0.08)
    lo, hi = lo - pad, hi + pad
    px = lambda v: x0 + 50 + (v - ss[0]) / max(1e-6, ss[-1] - ss[0]) * (x1 - x0 - 60)
    py = lambda v: y1 - 24 - (v - lo) / (hi - lo) * (y1 - y0 - 50)
    # grid: metres
    step = 10 ** np.floor(np.log10(max(hi - lo, 0.1)))
    for g in np.arange(np.ceil(lo / step) * step, hi, step):
        draw.line([(x0 + 50, py(g)), (x1 - 10, py(g))], fill=(235, 235, 235))
        draw.text((x0 + 4, py(g) - 6), f"{g:.1f}", fill=(120, 120, 120))
    draw.text((x1 - 140, y1 - 18), f"{ss[0]:.0f} - {ss[-1]:.0f} m along", fill=(120, 120, 120))
    for v, col in series:
        pts = [(px(a), py(b)) for a, b in zip(ss, v[m])]
        draw.line(pts, fill=col, width=2)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("area")
    ap.add_argument("--chain", type=int)
    ap.add_argument("--near", help="x,z: the chain passing closest to this point")
    args = ap.parse_args()
    d = os.path.join(ROOT, "Builds", "RoadProfile")
    chains = load(os.path.join(d, f"{args.area}_roads.csv"))
    if args.chain is not None:
        c = chains[args.chain]
    elif args.near:
        x0, z0 = (float(v) for v in args.near.split(","))
        c = min(chains.values(), key=lambda c: np.min(np.hypot(c["x"] - x0, c["z"] - z0)))
    else:
        cand = [c for c in chains.values() if c["ref"] == "A28" and c["group"] == "motorway"] or list(chains.values())
        c = max(cand, key=lambda c: c["s"][-1])
    s, b, a = c["s"], c["b"], c["a"]
    db, da = chord_dev(s, b), chord_dev(s, a)
    worst = s[np.argmax(np.abs(db))]
    win = (max(0, worst - 300), min(s[-1], worst + 300))
    W, H = 1600, 1150
    img = Image.new("RGB", (W, H), (248, 248, 246))
    dr = ImageDraw.Draw(img)
    dr.text((20, 10), f"{args.area} {c['ref'] or 'motorway'}: ground along the carriageway, before (red) / after (blue) road smoothing; "
                      f"chord deviation RMS {np.sqrt((db**2).mean())*100:.1f} -> {np.sqrt((da**2).mean())*100:.1f} cm, "
                      f"max {np.abs(db).max():.2f} -> {np.abs(da).max():.2f} m", fill=(0, 0, 0))
    red, blue = (215, 60, 50), (40, 90, 200)
    panel(dr, (20, 30, W - 20, 400), s, [(b, red), (a, blue)], f"full chain ({s[-1]/1000:.1f} km), height [m]")
    panel(dr, (20, 410, W - 20, 780), s, [(b, red), (a, blue)], f"600 m around the worst pothole (s = {worst:.0f} m), height [m]", win)
    panel(dr, (20, 790, W - 20, H - 20), s, [(db, red), (da, blue)], "deviation from the 5 m chord [m] (pothole depth / bump height)", win)
    out = os.path.join(d, f"{args.area}_A28.png" if not (args.chain is not None or args.near) else f"{args.area}_profile_{args.near or args.chain}.png")
    img.save(out)
    print(out)


if __name__ == "__main__":
    main()
