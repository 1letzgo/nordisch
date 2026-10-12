"""Area rectangle helpers shared by the fetch scripts.

An area is centred on (lat, lon) – the origin of the local game frame – and covers either a
square of size_m, or the rectangle given by "extent": {"west", "east", "south", "north"} in metres
from that origin (areas may be off-centre, e.g. Westerstede reaching far to the west).
"""
import math


def local_bounds(area):
    """(xMin, xMax, zMin, zMax) in local metres (x = east, z = north)."""
    e = area.get("extent")
    if e:
        return -float(e["west"]), float(e["east"]), -float(e["south"]), float(e["north"])
    h = area["size_m"] / 2.0
    return -h, h, -h, h


def latlon_bbox(area, margin=0.0):
    """(south, west, north, east) in degrees covering the rectangle plus margin metres."""
    x0, x1, z0, z1 = local_bounds(area)
    lat, lon = area["lat"], area["lon"]
    m_lat = 110574.0
    m_lon = 111320.0 * math.cos(math.radians(lat))
    return (lat + (z0 - margin) / m_lat, lon + (x0 - margin) / m_lon,
            lat + (z1 + margin) / m_lat, lon + (x1 + margin) / m_lon)
