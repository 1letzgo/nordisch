using UnityEngine;

namespace LeGamez.World
{
    /// <summary>
    /// Local equirectangular projection around an area centre. 1 Unity unit = 1 metre,
    /// +X = east, +Z = north. Accurate to well under 1 m inside a ~1 km area.
    /// </summary>
    [System.Serializable]
    public struct GeoProjection
    {
        const double MetersPerDegLat = 110574.0;
        const double MetersPerDegLonAtEquator = 111320.0;

        public double originLat;
        public double originLon;

        public GeoProjection(double lat, double lon)
        {
            originLat = lat;
            originLon = lon;
        }

        double MetersPerDegLon => MetersPerDegLonAtEquator * System.Math.Cos(originLat * System.Math.PI / 180.0);

        public Vector2 ToLocal(double lat, double lon)
        {
            return new Vector2(
                (float)((lon - originLon) * MetersPerDegLon),
                (float)((lat - originLat) * MetersPerDegLat));
        }

        public Vector3 ToWorld(double lat, double lon, float y = 0f)
        {
            var p = ToLocal(lat, lon);
            return new Vector3(p.x, y, p.y);
        }

        public void ToGeo(Vector3 world, out double lat, out double lon)
        {
            lat = originLat + world.z / MetersPerDegLat;
            lon = originLon + world.x / MetersPerDegLon;
        }
    }
}
