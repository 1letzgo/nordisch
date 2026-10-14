"""Area mask helpers shared by the fetch scripts (see make_mask.py).

Large areas (a whole municipality) are not built everywhere: Data/<id>.mask.png marks the inhabited parts
(settlements + corridors along the roads between them). Expensive data (LoD2, LiDAR trees, hedges, parcels) is only
fetched inside; terrain and aerial image cover the whole rectangle. Without a mask file everything is inside.

Mask PNG (8 bit grey, top row = north edge, like dop.png); pixel values:
    0   outside                      32  outside, forest (coarse terrain is coloured as forest)
    64  outside, lake / pond (coarse terrain is coloured as water; the OSM water area is dropped)
    160 landscape (band along a motorway: coarse terrain, but roads, water, barriers and LiDAR trees)
    176 motorway corridor (detailed terrain, roads, trees – still no houses, gardens or furniture)
    192 road corridor                255 settlement
Anything >= 128 counts as inside (roads, trees); houses, LoD2, parcels and hedges need >= 192 ("detail",
AreaMask.load(id, detail=True)). Data/<id>.mask.json: {"xMin", "zMin", "step", "width", "height", ...};
pixel (col, row) covers x in [xMin + col*step, +step), z in [zMax - (row+1)*step, zMax - row*step).
"""
import json
import urllib.request
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / "Data"

OUTSIDE, FOREST, WATER, LANDSCAPE, MOTORWAY, CORRIDOR, SETTLEMENT = 0, 32, 64, 160, 176, 192, 255
INSIDE, FINE, DETAIL = 128, 176, 192  # roads/trees, detailed terrain, houses


class AreaMask:
    def __init__(self, img, meta, level=128):
        self.img = img
        self.meta = meta
        self.x0, self.z0, self.step = float(meta["xMin"]), float(meta["zMin"]), float(meta["step"])
        self.h, self.w = img.shape
        self.z1 = self.z0 + self.h * self.step
        self.inside_img = img >= level

    @staticmethod
    def load(area_id, detail=False, level=None):
        """detail=True: only the parts where houses are built (>= 192) count as inside; level: any other threshold."""
        png, js = DATA / f"{area_id}.mask.png", DATA / f"{area_id}.mask.json"
        if not png.exists() or not js.exists():
            return None
        from PIL import Image
        return AreaMask(np.array(Image.open(png).convert("L")), json.loads(js.read_text()), level or (DETAIL if detail else INSIDE))

    def _index(self, x, z):
        c = np.floor((np.asarray(x, dtype=np.float64) - self.x0) / self.step).astype(np.int64)
        r = np.floor((self.z1 - np.asarray(z, dtype=np.float64)) / self.step).astype(np.int64)
        return c, r

    def inside(self, x, z):
        """Vectorised: True where (x, z) lies inside the mask (outside the raster = outside)."""
        c, r = np.broadcast_arrays(*self._index(x, z))
        ok = (c >= 0) & (c < self.w) & (r >= 0) & (r < self.h)
        out = np.zeros(np.shape(c), dtype=bool)
        out[ok] = self.inside_img[r[ok], c[ok]]
        return out

    def any_in_rect(self, x0, x1, z0, z1, margin=0.0):
        """True if any inside pixel touches the local rectangle (grown by margin)."""
        c0, r1 = self._index(x0 - margin, z0 - margin)
        c1, r0 = self._index(x1 + margin, z1 + margin)
        c0, c1 = max(int(c0), 0), min(int(c1), self.w - 1)
        r0, r1 = max(int(r0), 0), min(int(r1), self.h - 1)
        if c0 > c1 or r0 > r1:
            return False
        return bool(self.inside_img[r0:r1 + 1, c0:c1 + 1].any())

    def utm_tile_inside(self, key, frame, utm_to_latlon, size=1000.0, margin=60.0):
        """True if the UTM tile (east km, north km) of the given size touches the mask."""
        e0, n0 = key[0] * 1000.0, key[1] * 1000.0
        xs, zs = [], []
        for e, n in ((e0, n0), (e0 + size, n0), (e0, n0 + size), (e0 + size, n0 + size)):
            la, lo = utm_to_latlon(e, n)
            x, z = frame.to_local(la, lo)
            xs.append(x); zs.append(z)
        return self.any_in_rect(min(xs), max(xs), min(zs), max(zs), margin)


def stac_items(search_url, bbox, collection=None, limit=200):
    """All features of a STAC search (follows the "next" links – large areas have hundreds of tiles)."""
    url = search_url + "?" + (f"collections={collection}&" if collection else "") + f"limit={limit}&bbox=" + \
        ",".join(f"{v:.6f}" for v in bbox)
    while url:
        with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "Nordisch-stac/0.1"}), timeout=120) as r:
            page = json.load(r)
        yield from page["features"]
        url = next((l["href"] for l in page.get("links", []) if l.get("rel") == "next"), None)
        if not page["features"]:
            break
