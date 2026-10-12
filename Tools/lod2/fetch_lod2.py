#!/usr/bin/env python3
"""Fetch LGLN LoD2 3D building models (CityGML) for the curated game areas.

Usage:
    python3 Tools/lod2/fetch_lod2.py            # all areas
    python3 Tools/lod2/fetch_lod2.py oldenburg  # single area

Tiles are found via the LGLN STAC API, streamed and parsed on the fly (no raw files kept),
converted from ETRS89/UTM32 to the same local metric frame the Unity importer uses
(equirectangular around the area centre, x = east, z = north, y = up, ground = 0),
and written to Data/<id>.lod2.json.

Data: 3D-Gebäudemodelle LoD2, © LGLN (Landesamt für Geoinformation und Landesvermessung
Niedersachsen), CC BY 4.0 / dl-de/by-2-0.
"""
import json
import math
import sys
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "osm"))
from area_bounds import latlon_bbox, local_bounds  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
AREAS_FILE = ROOT / "Tools" / "osm" / "areas.json"
OUT_DIR = ROOT / "Data"
STAC = "https://lod.stac.lgln.niedersachsen.de/search"
MARGIN_M = 40.0

NS = {
    "gml": "http://www.opengis.net/gml",
    "bldg": "http://www.opengis.net/citygml/building/1.0",
}
SURFACES = {"RoofSurface": "R", "WallSurface": "W", "GroundSurface": "G", "ClosureSurface": "C"}


# ---------------------------------------------------------------- projection

def utm_to_latlon(e, n, zone=32):
    """Inverse transverse Mercator (GRS80), accurate to mm in-zone."""
    a = 6378137.0
    f = 1 / 298.257222101
    k0 = 0.9996
    e2 = f * (2 - f)
    ep2 = e2 / (1 - e2)
    x = e - 500000.0
    y = n
    m = y / k0
    mu = m / (a * (1 - e2 / 4 - 3 * e2 ** 2 / 64 - 5 * e2 ** 3 / 256))
    e1 = (1 - math.sqrt(1 - e2)) / (1 + math.sqrt(1 - e2))
    phi1 = (mu + (3 * e1 / 2 - 27 * e1 ** 3 / 32) * math.sin(2 * mu)
            + (21 * e1 ** 2 / 16 - 55 * e1 ** 4 / 32) * math.sin(4 * mu)
            + (151 * e1 ** 3 / 96) * math.sin(6 * mu)
            + (1097 * e1 ** 4 / 512) * math.sin(8 * mu))
    s, c, t = math.sin(phi1), math.cos(phi1), math.tan(phi1)
    n1 = a / math.sqrt(1 - e2 * s * s)
    t1 = t * t
    c1 = ep2 * c * c
    r1 = a * (1 - e2) / (1 - e2 * s * s) ** 1.5
    d = x / (n1 * k0)
    lat = phi1 - (n1 * t / r1) * (d * d / 2 - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * ep2) * d ** 4 / 24
                                  + (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * ep2 - 3 * c1 * c1) * d ** 6 / 720)
    lon = (d - (1 + 2 * t1 + c1) * d ** 3 / 6
           + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * ep2 + 24 * t1 * t1) * d ** 5 / 120) / c
    lon0 = math.radians((zone - 1) * 6 - 180 + 3)
    return math.degrees(lat), math.degrees(lon0 + lon)


class LocalFrame:
    """Mirror of GeoProjection.cs (equirectangular around the area centre)."""

    def __init__(self, lat0, lon0):
        self.lat0, self.lon0 = lat0, lon0
        self.m_lat = 110574.0
        self.m_lon = 111320.0 * math.cos(math.radians(lat0))

    def to_local(self, lat, lon):
        return (lon - self.lon0) * self.m_lon, (lat - self.lat0) * self.m_lat


# ---------------------------------------------------------------- CityGML parsing

def parse_poslist(text):
    v = [float(t) for t in text.split()]
    return [v[i:i + 3] for i in range(0, len(v) - 2, 3)]


def polygon_rings(poly):
    ext = poly.find("gml:exterior//gml:posList", NS)
    if ext is None:
        return None, []
    holes = [parse_poslist(p.text) for p in poly.findall("gml:interior//gml:posList", NS)]
    return parse_poslist(ext.text), holes


def text_of(el, path):
    x = el.find(path, NS)
    return x.text.strip() if x is not None and x.text else None


def extract_building(b):
    """Returns (attrs, surfaces) for a bldg:Building or bldg:BuildingPart (own surfaces only)."""
    attrs = {
        "id": b.get("{http://www.opengis.net/gml}id"),
        "function": text_of(b, "bldg:function"),
        "roofType": text_of(b, "bldg:roofType"),
        "height": text_of(b, "bldg:measuredHeight"),
    }
    surfaces = []
    for bound in b.findall("bldg:boundedBy", NS):
        for child in bound:
            kind = SURFACES.get(child.tag.split("}")[1])
            if kind is None:
                continue
            for poly in child.iter("{http://www.opengis.net/gml}Polygon"):
                ext, holes = polygon_rings(poly)
                if ext and len(ext) >= 4:
                    surfaces.append((kind, ext, holes))
    return attrs, surfaces


# ---------------------------------------------------------------- main

def find_tiles(bbox):
    url = STAC + "?collections=lod2&limit=50&bbox=" + ",".join(f"{v:.6f}" for v in bbox)
    with urllib.request.urlopen(url, timeout=60) as r:
        data = json.load(r)
    return [f["assets"]["lod2-gml"]["href"] for f in data["features"] if "lod2-gml" in f["assets"]]


def process_area(area):
    lat0, lon0 = area["lat"], area["lon"]
    frame = LocalFrame(lat0, lon0)
    x0, x1, z0, z1 = local_bounds(area)
    s, w, n, e = latlon_bbox(area, MARGIN_M)
    tiles = find_tiles([w, s, e, n])
    print(f"[{area['id']}] {len(tiles)} tiles", flush=True)

    buildings = []
    seen = set()
    for href in tiles:
        print("   ", href.rsplit("/", 1)[-1], flush=True)
        with urllib.request.urlopen(urllib.request.Request(href, headers={"User-Agent": "Nordisch-LoD2/0.1"}), timeout=300) as resp:
            for _, el in ET.iterparse(resp, events=("end",)):
                if el.tag != "{http://www.opengis.net/citygml/building/1.0}Building":
                    continue
                parts = [el] + el.findall(".//bldg:BuildingPart", NS)
                for part in parts:
                    attrs, surfaces = extract_building(part)
                    if not surfaces or attrs["id"] in seen:
                        continue
                    # Convert every vertex; keep the building if its footprint centre is inside the area.
                    conv = []
                    zmin = min(p[2] for _, ring, _ in surfaces for p in ring)
                    for kind, ring, holes in surfaces:
                        def tr(pts):
                            out = []
                            for e, n, z in pts:
                                la, lo = utm_to_latlon(e, n)
                                x, y = frame.to_local(la, lo)
                                out.append([round(x, 2), round(z - zmin, 2), round(y, 2)])
                            return out
                        conv.append({"t": kind, "r": tr(ring), "h": [tr(h) for h in holes]})
                    pts = [p for s in conv for p in s["r"]]
                    cx = sum(p[0] for p in pts) / len(pts)
                    cz = sum(p[2] for p in pts) / len(pts)
                    if not (x0 - MARGIN_M <= cx <= x1 + MARGIN_M and z0 - MARGIN_M <= cz <= z1 + MARGIN_M):
                        continue
                    seen.add(attrs["id"])
                    buildings.append({**{k: v for k, v in attrs.items() if v}, "s": conv})
                el.clear()

    out = OUT_DIR / f"{area['id']}.lod2.json"
    meta = {"id": area["id"], "center": [lat0, lon0], "attribution": "3D-Gebäude © LGLN (CC BY 4.0)"}
    out.write_text(json.dumps({"meta": meta, "buildings": buildings}, separators=(",", ":")))
    print(f"  -> {out.relative_to(ROOT)}: {len(buildings)} buildings, {out.stat().st_size // 1024} KB", flush=True)


def main(argv):
    areas = json.loads(AREAS_FILE.read_text())["areas"]
    wanted = set(argv[1:])
    for a in areas:
        if not wanted or a["id"] in wanted:
            process_area(a)


if __name__ == "__main__":
    main(sys.argv)
