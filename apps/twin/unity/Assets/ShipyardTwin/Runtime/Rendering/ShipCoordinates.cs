using System;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Axis conventions. Ship: x forward from the aft end, y to port, z up (right-handed, metres).
    /// Yard: crane telemetry x/y in the ground plane, z up (right-handed). Unity: x, y up, z (left-handed).
    /// Both right-handed frames map to Unity as (x, y, z) → (x, z, y): seen from above, x stays to the right
    /// and y points "up the screen", so nothing is mirrored. OBJ models from generate_models.py use
    /// (ship x, ship z, -ship y), which maps to Unity as (x, y, -z) with the triangle winding reversed.
    /// </summary>
    public static class ShipCoordinates
    {
        public static Vector3 ShipToUnity(float x, float y, float z) => new Vector3(x, z, y);

        public static Vector3 YardToUnity(float x, float y, float z) => new Vector3(x, z, y);

        public static Vector3 ObjToUnity(float x, float y, float z) => new Vector3(x, y, -z);

        /// <summary>Yard heading (degrees from yard x toward yard y) as a Unity rotation about +Y.</summary>
        public static Quaternion HeadingToUnity(float headingDeg) => Quaternion.Euler(0f, -headingDeg, 0f);

        public static Vector3 CentreOfGravity(BlockRecord block) => ShipToUnity(block.cog_x, block.cog_y, block.cog_z);

        /// <summary>Block box in Unity axes relative to its centre of gravity.</summary>
        public static Bounds LocalBounds(BlockRecord block)
        {
            var min = ShipToUnity(block.x_min - block.cog_x, block.y_min - block.cog_y, block.z_min - block.cog_z);
            var max = ShipToUnity(block.x_max - block.cog_x, block.y_max - block.cog_y, block.z_max - block.cog_z);
            var bounds = new Bounds(min, Vector3.zero);
            bounds.Encapsulate(max);
            return bounds;
        }

        public const double EarthRadius = 6378137.0;

        /// <summary>WGS84 → yard metres for a site (local tangent plane; inverse of the ERP's yard_to_geo).</summary>
        public static Vector2 GeoToYard(YardSite site, double lat, double lon)
        {
            var north = (lat - site.lat) * Math.PI / 180.0 * EarthRadius;
            var east = (lon - site.lon) * Math.PI / 180.0 * EarthRadius * Math.Cos(site.lat * Math.PI / 180.0);
            var b = site.x_axis_bearing_deg * Math.PI / 180.0;
            return new Vector2((float)(east * Math.Sin(b) + north * Math.Cos(b)), (float)(-east * Math.Cos(b) + north * Math.Sin(b)));
        }

        /// <summary>Yard metres → WGS84 (same formula as the ERP).</summary>
        public static (double Lat, double Lon) YardToGeo(YardSite site, float x, float y)
        {
            var b = site.x_axis_bearing_deg * Math.PI / 180.0;
            var east = x * Math.Sin(b) - y * Math.Cos(b);
            var north = x * Math.Cos(b) + y * Math.Sin(b);
            return (site.lat + north / EarthRadius * 180.0 / Math.PI,
                    site.lon + east / (EarthRadius * Math.Cos(site.lat * Math.PI / 180.0)) * 180.0 / Math.PI);
        }

        /// <summary>WGS84 → Web Mercator global pixel (256-pixel tiles at a zoom).</summary>
        public static (double X, double Y) GeoToMercatorPixel(int zoom, double lat, double lon)
        {
            var n = 256.0 * Math.Pow(2, zoom);
            var phi = lat * Math.PI / 180.0;
            return ((lon + 180.0) / 360.0 * n, (1 - Math.Log(Math.Tan(phi) + 1 / Math.Cos(phi)) / Math.PI) / 2 * n);
        }

        /// <summary>Web Mercator global pixel (256-pixel tiles at a zoom) → WGS84.</summary>
        public static (double Lat, double Lon) MercatorPixelToGeo(int zoom, double px, double py)
        {
            var n = 256.0 * Math.Pow(2, zoom);
            var lon = px / n * 360.0 - 180.0;
            var lat = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * py / n))) * 180.0 / Math.PI;
            return (lat, lon);
        }

        /// <summary>Unity pose of the ship origin on its dock.</summary>
        public static Pose HullPose(HullPlacement placement) =>
            new Pose(YardToUnity(placement.origin_x, placement.origin_y, placement.origin_z), HeadingToUnity(placement.heading_deg));
    }
}
