using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>Small 2D polygon helpers for the paved yard outline (yard metres, counter-clockwise).</summary>
    public static class Polygon2D
    {
        /// <summary>Convex hull (Andrew's monotone chain), counter-clockwise, no repeated last point.</summary>
        public static List<Vector2> ConvexHull(IEnumerable<Vector2> points)
        {
            var p = points.Distinct().OrderBy(v => v.x).ThenBy(v => v.y).ToList();
            if (p.Count < 3)
            {
                return p;
            }

            float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            var hull = new List<Vector2>();
            foreach (var pass in new[] { p, Enumerable.Reverse(p).ToList() })
            {
                var start = hull.Count;
                foreach (var q in pass)
                {
                    while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], q) <= 0)
                    {
                        hull.RemoveAt(hull.Count - 1);
                    }

                    hull.Add(q);
                }

                hull.RemoveAt(hull.Count - 1);
            }

            return hull;
        }

        /// <summary>Sutherland–Hodgman: clips a polygon by a convex counter-clockwise polygon.</summary>
        public static List<Vector2> ClipConvex(List<Vector2> subject, List<Vector2> clip)
        {
            var output = subject;
            for (var i = 0; i < clip.Count && output.Count > 0; i++)
            {
                Vector2 a = clip[i], b = clip[(i + 1) % clip.Count];
                bool Inside(Vector2 p) => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x) >= 0;
                Vector2 Cut(Vector2 p, Vector2 q)
                {
                    var d1 = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
                    var d2 = (b.x - a.x) * (q.y - a.y) - (b.y - a.y) * (q.x - a.x);
                    return p + (q - p) * (d1 / (d1 - d2));
                }

                var input = output;
                output = new List<Vector2>();
                for (var j = 0; j < input.Count; j++)
                {
                    Vector2 p = input[j], q = input[(j + 1) % input.Count];
                    if (Inside(q))
                    {
                        if (!Inside(p)) output.Add(Cut(p, q));
                        output.Add(q);
                    }
                    else if (Inside(p))
                    {
                        output.Add(Cut(p, q));
                    }
                }
            }

            return output;
        }

        /// <summary>Point inside a convex counter-clockwise polygon (edges count as inside).</summary>
        public static bool Contains(List<Vector2> polygon, Vector2 p)
        {
            for (var i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                if ((b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x) < 0)
                {
                    return false;
                }
            }

            return polygon.Count >= 3;
        }

        /// <summary>Distance from a point to a convex polygon (0 inside).</summary>
        public static float Distance(List<Vector2> polygon, Vector2 p)
        {
            if (Contains(polygon, p))
            {
                return 0f;
            }

            var best = float.MaxValue;
            for (var i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Count], ab = b - a;
                var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }

            return best;
        }

        public static List<Vector2> Rect(float x0, float x1, float y0, float y1) =>
            new List<Vector2> { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) };

        /// <summary>Flat fan mesh for a convex yard polygon at a height, UVs in metres, facing up.</summary>
        public static Mesh Flat(List<Vector2> polygon, float height)
        {
            var vertices = polygon.Select(v => ShipCoordinates.YardToUnity(v.x, v.y, height)).ToList();
            var triangles = new List<int>();
            for (var i = 1; i + 1 < polygon.Count; i++)
            {
                // Counter-clockwise seen from above stays so in Unity; Unity front faces are clockwise: reverse.
                triangles.AddRange(new[] { 0, i + 1, i });
            }

            var mesh = new Mesh { name = "Ground" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetUVs(0, vertices.Select(v => new Vector2(v.x, v.z)).ToList());
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
