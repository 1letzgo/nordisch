#!/usr/bin/env python3
"""Fetch traffic control from OpenStreetMap for the game areas: traffic lights, crossings, stop / give-way
signs and mapped traffic signs (nodes). Kept separate from <id>.osm.json so a refresh here never shifts the
ids of generated objects (map editor edits stay valid).

Usage:
    python3 Tools/osm/fetch_traffic.py              # all areas
    python3 Tools/osm/fetch_traffic.py westerstede  # single area

Writes Data/<id>.traffic.json: {"nodes": [{"lat", "lon", "tags"}, ...]}.
Data (c) OpenStreetMap contributors, ODbL.
"""
import json
import sys
import time
from pathlib import Path

from area_bounds import latlon_bbox
from fetch_area import fetch, AREAS_FILE, OUT_DIR, ROOT


def query(s, w, n, e):
    b = f"{s:.6f},{w:.6f},{n:.6f},{e:.6f}"
    return f"""
[out:json][timeout:300];
(
  node["highway"~"^(traffic_signals|crossing|stop|give_way)$"]({b});
  node["traffic_sign"]({b});
  node["crossing"]({b});
  node["highway"="motorway_junction"]({b});
);
out body;
"""


def main(argv):
    areas = json.loads(AREAS_FILE.read_text())["areas"]
    wanted = set(argv[1:])
    for a in areas:
        if wanted and a["id"] not in wanted:
            continue
        s, w, n, e = latlon_bbox(a, 30)
        res = fetch(query(s, w, n, e))
        nodes = [{"lat": el["lat"], "lon": el["lon"], "tags": el.get("tags", {})} for el in res["elements"] if el["type"] == "node"]
        out = OUT_DIR / f"{a['id']}.traffic.json"
        out.write_text(json.dumps({"source": "© OpenStreetMap contributors (ODbL)", "nodes": nodes}, ensure_ascii=False, separators=(",", ":")))
        kinds = {}
        for nd in nodes:
            k = nd["tags"].get("highway") or ("traffic_sign" if "traffic_sign" in nd["tags"] else "other")
            kinds[k] = kinds.get(k, 0) + 1
        print(f"[{a['id']}] -> {out.relative_to(ROOT)}: {len(nodes)} nodes {kinds}", flush=True)
        time.sleep(2)


if __name__ == "__main__":
    main(sys.argv)
