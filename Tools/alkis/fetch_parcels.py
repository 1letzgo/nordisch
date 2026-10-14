#!/usr/bin/env python3
"""Fetch ALKIS cadastral parcels (Flurstücke) for the game areas from the LGLN INSPIRE WFS and convert
them to the local game frame – the importer derives garden fences and hedges from the parcel borders.

Usage:
    python3 Tools/alkis/fetch_parcels.py              # all areas
    python3 Tools/alkis/fetch_parcels.py westerstede  # single area

Output Data/<id>.parcels.json:
    {"meta": {...}, "parcels": [{"id": "03086304600316", "area": 931.0, "ring": [[x, z], ...]}, ...]}
    x = east, z = north in metres from the area centre (same frame as OSM/LoD2), outer ring only.

Data: ALKIS Flurstücke, © LGLN (Landesamt für Geoinformation und Landesvermessung Niedersachsen),
CC BY 4.0 / dl-de/by-2-0 – via INSPIRE-WFS "Flurstücke/Grundstücke ALKIS".
"""
import json
import sys
import time
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lod2"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "osm"))
from fetch_lod2 import LocalFrame  # noqa: E402  (same projection as the importer)
from area_bounds import latlon_bbox, local_bounds  # noqa: E402
from area_mask import AreaMask  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
AREAS_FILE = ROOT / "Tools" / "osm" / "areas.json"
OUT_DIR = ROOT / "Data"
WFS = "https://www.inspire.niedersachsen.de/doorman/noauth/alkis-dls-cp"
MARGIN_M = 40.0
TILE_DEG = 0.004          # ~450 x 270 m per request
PAGE = 2000

NS = {"wfs": "http://www.opengis.net/wfs/2.0", "gml": "http://www.opengis.net/gml/3.2",
      "cp": "http://inspire.ec.europa.eu/schemas/cp/4.0"}


def get(url, tries=4):
    for i in range(tries):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "Nordisch-ALKIS/0.1"})
            with urllib.request.urlopen(req, timeout=180) as r:
                return r.read()
        except Exception as e:  # noqa: BLE001 – flaky public service, retry
            print(f"      retry {i + 1}: {e}", flush=True)
            time.sleep(3 * (i + 1))
    raise RuntimeError("WFS request failed: " + url)


def fetch_tile(s, w, n, e):
    """All parcels touching the box (lat/lon), paged."""
    start = 0
    while True:
        url = (f"{WFS}?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature&TYPENAMES=cp:CadastralParcel"
               f"&SRSNAME=urn:ogc:def:crs:EPSG::4326&COUNT={PAGE}&STARTINDEX={start}"
               f"&BBOX={s:.6f},{w:.6f},{n:.6f},{e:.6f},urn:ogc:def:crs:EPSG::4326")
        root = ET.fromstring(get(url))
        members = root.findall("wfs:member", NS)
        for m in members:
            yield m
        if len(members) < PAGE:
            return
        start += PAGE


def parse(member, frame):
    p = member.find("cp:CadastralParcel", NS)
    if p is None:
        return None
    ref = p.findtext("cp:nationalCadastralReference", default="", namespaces=NS).rstrip("_")
    area = float(p.findtext("cp:areaValue", default="0", namespaces=NS) or 0)
    pos = p.find("cp:geometry//gml:exterior//gml:posList", NS)
    if pos is None or not pos.text:
        return None
    v = [float(t) for t in pos.text.split()]
    ring = []
    for i in range(0, len(v) - 1, 2):
        x, z = frame.to_local(v[i], v[i + 1])  # WFS axis order: lat, lon
        ring.append([round(x, 2), round(z, 2)])
    if len(ring) > 3 and ring[0] == ring[-1]:
        ring.pop()
    return {"id": ref, "area": area, "ring": ring} if len(ring) >= 3 else None


def process_area(area):
    frame = LocalFrame(area["lat"], area["lon"])
    x0, x1, z0, z1 = local_bounds(area)
    s, w, n, e = latlon_bbox(area, MARGIN_M)
    parcels, seen = [], set()
    mask = AreaMask.load(area["id"], detail=True)  # large areas: only requests touching the mask (make_mask.py; houses only where the mask is detailed)
    lat = s
    tiles = 0
    while lat < n:
        lon = w
        while lon < e:
            if mask is not None:
                tx0, tz0 = frame.to_local(lat, lon)
                tx1, tz1 = frame.to_local(min(lat + TILE_DEG, n), min(lon + TILE_DEG * 1.6, e))
                if not mask.any_in_rect(tx0, tx1, tz0, tz1, 20):
                    lon += TILE_DEG * 1.6
                    continue
            tiles += 1
            for m in fetch_tile(lat, lon, min(lat + TILE_DEG, n), min(lon + TILE_DEG * 1.6, e)):
                pr = parse(m, frame)
                if pr is None or pr["id"] in seen:
                    continue
                cx = sum(p[0] for p in pr["ring"]) / len(pr["ring"])
                cz = sum(p[1] for p in pr["ring"]) / len(pr["ring"])
                if not (x0 - MARGIN_M <= cx <= x1 + MARGIN_M and z0 - MARGIN_M <= cz <= z1 + MARGIN_M):
                    continue
                if mask is not None and not mask.inside(cx, cz):
                    continue
                seen.add(pr["id"])
                parcels.append(pr)
            lon += TILE_DEG * 1.6
        lat += TILE_DEG
        print(f"  [{area['id']}] {tiles} tiles, {len(parcels)} parcels", flush=True)
    out = OUT_DIR / f"{area['id']}.parcels.json"
    meta = {"id": area["id"], "center": [area["lat"], area["lon"]], "attribution": "Flurstücke © LGLN (CC BY 4.0)"}
    out.write_text(json.dumps({"meta": meta, "parcels": parcels}, separators=(",", ":")))
    print(f"  -> {out.relative_to(ROOT)}: {len(parcels)} parcels, {out.stat().st_size // 1024} KB", flush=True)


def main(argv):
    areas = json.loads(AREAS_FILE.read_text())["areas"]
    wanted = set(argv[1:])
    for a in areas:
        if not wanted or a["id"] in wanted:
            process_area(a)


if __name__ == "__main__":
    main(sys.argv)
