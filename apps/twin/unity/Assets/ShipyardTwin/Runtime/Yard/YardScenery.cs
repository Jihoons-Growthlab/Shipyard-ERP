using System;
using System.Collections.Generic;
using System.Linq;
using GrowthLab.Core;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>Scenery plan written by pipeline/generate_scenery.py (yard metres, x seaward, y along shore).</summary>
    [Serializable]
    public sealed class SceneryFile
    {
        public string format;
        public Road[] roads = new Road[0];
        public PropPlacement[] props = new PropPlacement[0];
        public Route[] routes = new Route[0];

        public static SceneryFile Parse(string json)
        {
            var file = JsonUtility.FromJson<SceneryFile>(json);
            if (file == null || file.format != "shipyard-scenery/1")
            {
                throw new FormatException("Not a shipyard-scenery/1 file (run pipeline/generate_scenery.py).");
            }

            file.roads ??= new Road[0];
            file.props ??= new PropPlacement[0];
            file.routes ??= new Route[0];
            return file;
        }
    }

    [Serializable]
    public sealed class Road
    {
        public float x0, y0, x1, y1, width;
    }

    [Serializable]
    public sealed class PropPlacement
    {
        public string model;
        public float x, y, heading;
    }

    /// <summary>A vehicle driving along yard points; JsonUtility cannot read nested arrays, so points come flattened.</summary>
    [Serializable]
    public sealed class Route
    {
        public string model;
        public float speed;
        public bool loop;
        public float[] path = new float[0];

        public Vector2[] Points()
        {
            var points = new Vector2[path.Length / 2];
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = new Vector2(path[2 * i], path[2 * i + 1]);
            }

            return points;
        }
    }

    /// <summary>Position and heading along a polyline; closed loops wrap, open paths drive back and forth.</summary>
    public static class RouteMath
    {
        public static float Length(IReadOnlyList<Vector2> points, bool loop)
        {
            var length = 0f;
            var count = loop ? points.Count : points.Count - 1;
            for (var i = 0; i < count; i++)
            {
                length += Vector2.Distance(points[i], points[(i + 1) % points.Count]);
            }

            return length;
        }

        /// <summary>Yard position and heading (degrees from +x toward +y) at a travelled distance.</summary>
        public static (Vector2 Position, float Heading) At(IReadOnlyList<Vector2> points, bool loop, float distance)
        {
            if (points.Count < 2)
            {
                return (points.Count == 1 ? points[0] : Vector2.zero, 0f);
            }

            var total = Length(points, loop);
            var reverse = false;
            if (loop)
            {
                distance = Mathf.Repeat(distance, total);
            }
            else
            {
                distance = Mathf.Repeat(distance, 2f * total);
                if (distance > total)
                {
                    distance = 2f * total - distance;
                    reverse = true;
                }
            }

            var count = loop ? points.Count : points.Count - 1;
            for (var i = 0; i < count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                var segment = Vector2.Distance(a, b);
                if (distance <= segment || i == count - 1)
                {
                    var direction = segment > 1e-4f ? (b - a) / segment : Vector2.right;
                    if (reverse)
                    {
                        direction = -direction;
                    }

                    var heading = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
                    return (a + (b - a) * Mathf.Clamp01(segment > 1e-4f ? distance / segment : 0f), Mathf.Repeat(heading, 360f));
                }

                distance -= segment;
            }

            return (points[points.Count - 1], 0f);
        }
    }

    /// <summary>Shared meshes for generated props (Resources/ShipyardTwin/Props/&lt;model&gt;.obj.bytes), parsed once.</summary>
    public static class PropLibrary
    {
        private static readonly Dictionary<string, (Mesh Mesh, string[] Paints)> Cache = new Dictionary<string, (Mesh, string[])>();

        public const string Folder = "ShipyardTwin/Props/";

        /// <summary>Shared mesh and paint names of a model, for instancing or mesh combining.</summary>
        public static bool TryGet(string model, out Mesh mesh, out string[] paints)
        {
            mesh = null;
            paints = null;
            if (!Load(model, out var entry))
            {
                return false;
            }

            (mesh, paints) = entry;
            return true;
        }

        private static bool Load(string model, out (Mesh Mesh, string[] Paints) entry)
        {
            if (!Cache.TryGetValue(model, out entry))
            {
                var text = Resources.Load<TextAsset>(Folder + model + ".obj");
                if (text == null)
                {
                    return false;
                }

                var parsed = ObjMeshParser.Parse(text.text);
                Resources.UnloadAsset(text);
                entry = (ObjMeshParser.ToMesh(parsed, model), parsed.Materials.ToArray());
                Cache[model] = entry;
            }

            return true;
        }

        /// <summary>Many copies of a model merged into one renderer (one submesh per paint); false when missing.</summary>
        public static bool Combine(string model, IReadOnlyList<Matrix4x4> placements, ShipPalette palette, Transform parent, string name)
        {
            if (placements.Count == 0 || !Load(model, out var entry))
            {
                return false;
            }

            var parts = new CombineInstance[entry.Mesh.subMeshCount];
            for (var k = 0; k < parts.Length; k++)
            {
                var part = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                part.CombineMeshes(placements.Select(m => new CombineInstance { mesh = entry.Mesh, subMeshIndex = k, transform = m }).ToArray(), true, true);
                parts[k] = new CombineInstance { mesh = part, transform = Matrix4x4.identity };
            }

            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.CombineMeshes(parts, false, false);
            var go = new GameObject(name + " " + model);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = entry.Paints.Select(palette.Get).ToArray();
            return true;
        }

        public static bool TryCreate(string model, ShipPalette palette, Transform parent, out GameObject instance)
        {
            instance = null;
            if (!Load(model, out var entry))
            {
                return false;
            }

            instance = new GameObject(model);
            instance.transform.SetParent(parent, false);
            instance.AddComponent<MeshFilter>().sharedMesh = entry.Mesh;
            instance.AddComponent<MeshRenderer>().sharedMaterials = entry.Paints.Select(palette.Get).ToArray();
            return true;
        }
    }

    /// <summary>
    /// Roads with lane markings, parked props (office, gatehouse, cars, light towers, quay cranes, stock) and
    /// vehicles driving their routes, from Resources/ShipyardTwin/scenery.json. Purely visual: nothing here comes
    /// from the ERP; regenerate with pipeline/generate_scenery.py when the yard layout changes.
    /// </summary>
    public sealed class YardScenery : MonoBehaviour, ICoreConsumer
    {
        public const string Resource = "ShipyardTwin/scenery";

        /// <summary>Scenery buildings that are not ERP facilities, named for the hover tooltip.</summary>
        private static readonly Dictionary<string, string> Buildings = new Dictionary<string, string>
        {
            { "office", "사무동" }, { "gatehouse", "정문 경비실" },
        };

        private readonly List<(Transform Vehicle, Vector2[] Points, bool Loop, float Speed, float Offset)> _vehicles =
            new List<(Transform, Vector2[], bool, float, float)>();

        private ShipPalette _palette;
        private float _time;

        public void Construct(CoreContext context)
        {
            if (_palette != null || !context.Services.TryGet(out _palette))
            {
                return;
            }

            var asset = Resources.Load<TextAsset>(Resource);
            if (asset == null)
            {
                context.Log.Warning("ShipyardTwin", "No scenery (Resources/" + Resource + "); run pipeline/generate_scenery.py.");
                return;
            }

            var scenery = SceneryFile.Parse(asset.text);
            // Wider roads sit a little higher, so at junctions one surface wins instead of two flickering.
            var order = scenery.roads.OrderBy(r => r.width).ToList();
            for (var i = 0; i < order.Count; i++)
            {
                BuildRoad(order[i], 0.004f * i, scenery.roads);
            }

            var missing = 0;
            foreach (var prop in scenery.props.Where(p => !p.model.StartsWith("GY-", StringComparison.Ordinal)))
            {
                if (PropLibrary.TryCreate(prop.model, _palette, transform, out var go))
                {
                    Place(go.transform, new Vector2(prop.x, prop.y), prop.heading);
                    if (Buildings.TryGetValue(prop.model, out var label))
                    {
                        HoverTarget.Attach(go, "시설", label, "부대 건물 (ERP 시설 아님)");
                    }
                }
                else
                {
                    missing++;
                }
            }

            for (var i = 0; i < scenery.routes.Length; i++)
            {
                var route = scenery.routes[i];
                var points = route.Points();
                if (points.Length >= 2 && PropLibrary.TryCreate(route.model, _palette, transform, out var go))
                {
                    _vehicles.Add((go.transform, points, route.loop, Mathf.Max(0.1f, route.speed), i * 37f));
                }
            }

            if (missing > 0)
            {
                context.Log.Warning("ShipyardTwin", missing + " scenery models missing.");
            }

            Update();
        }

        private void Update()
        {
            _time += Time.deltaTime;
            foreach (var (vehicle, points, loop, speed, offset) in _vehicles)
            {
                var (position, heading) = RouteMath.At(points, loop, offset + _time * speed);
                Place(vehicle, position, heading);
            }
        }

        private static void Place(Transform t, Vector2 yard, float heading)
        {
            t.SetPositionAndRotation(ShipCoordinates.YardToUnity(yard.x, yard.y, YardLayout.QuayLevel), ShipCoordinates.HeadingToUnity(heading));
        }

        /// <summary>Rendered road surface in yard metres: the centre line plus half a width past each end (fills corners).</summary>
        public static (Vector2 A, Vector2 B, Vector2 Along, Vector2 Across, float HalfLength, float HalfWidth) Surface(Road road)
        {
            var a = new Vector2(road.x0, road.y0);
            var b = new Vector2(road.x1, road.y1);
            var along = (b - a).sqrMagnitude > 1e-6f ? (b - a).normalized : Vector2.right;
            return (a, b, along, new Vector2(-along.y, along.x), Vector2.Distance(a, b) / 2f + road.width / 2f, road.width / 2f);
        }

        /// <summary>True when a yard point lies on another road's surface (a junction), with a small margin.</summary>
        public static bool OnOtherRoad(Vector2 p, Road self, IEnumerable<Road> roads, float margin = 0.3f)
        {
            foreach (var other in roads)
            {
                if (ReferenceEquals(other, self))
                {
                    continue;
                }

                var s = Surface(other);
                var d = p - (s.A + s.B) / 2f;
                if (Mathf.Abs(Vector2.Dot(d, s.Along)) <= s.HalfLength + margin && Mathf.Abs(Vector2.Dot(d, s.Across)) <= s.HalfWidth + margin)
                {
                    return true;
                }
            }

            return false;
        }

        private void BuildRoad(Road road, float lift, IReadOnlyList<Road> roads)
        {
            var level = YardLayout.QuayLevel + 0.05f + lift;
            var a = ShipCoordinates.YardToUnity(road.x0, road.y0, level);
            var b = ShipCoordinates.YardToUnity(road.x1, road.y1, level);
            var length = Vector3.Distance(a, b);
            if (length < 0.5f)
            {
                return;
            }

            var rotation = Quaternion.LookRotation(b - a, Vector3.up);
            Strip("Road", (a + b) / 2f, rotation, new Vector3(road.width, 0.1f, length + road.width), "road");
            var start = new Vector2(road.x0, road.y0);
            var dir = (new Vector2(road.x1, road.y1) - start) / length;
            var across = new Vector2(-dir.y, dir.x);
            // Dashed centre line, left out inside junctions.
            for (var d = 3f; d < length - 3f; d += 9f)
            {
                if (OnOtherRoad(start + dir * (d + 2f), road, roads) || OnOtherRoad(start + dir * (d - 2f), road, roads))
                {
                    continue;
                }

                Strip("CentreLine", a + (b - a) * (d / length) + Vector3.up * 0.06f, rotation, new Vector3(0.18f, 0.02f, 4f), "road_marking");
            }

            // Solid edge lines, broken where another road joins or crosses (sampled every 0.5 m).
            foreach (var side in new[] { -1f, 1f })
            {
                var offset = across * side * (road.width / 2f - 0.4f);
                float? runStart = null;
                for (var d = 0f; d <= length + 0.01f; d += 0.5f)
                {
                    var free = !OnOtherRoad(start + dir * d + offset, road, roads);
                    if (free && runStart == null)
                    {
                        runStart = d;
                    }

                    if ((!free || d + 0.5f > length + 0.01f) && runStart != null)
                    {
                        var end = free ? length : d - 0.5f;
                        if (end - runStart.Value > 1f)
                        {
                            var mid = start + dir * ((runStart.Value + end) / 2f) + offset;
                            Strip("EdgeLine", ShipCoordinates.YardToUnity(mid.x, mid.y, level + 0.06f), rotation,
                                new Vector3(0.14f, 0.02f, end - runStart.Value), "road_marking");
                        }

                        runStart = null;
                    }
                }
            }
        }

        private void Strip(string name, Vector3 position, Quaternion rotation, Vector3 size, string paint)
        {
            var strip = MetricBox.Create(name, transform, size, _palette.Get(paint));
            strip.transform.SetPositionAndRotation(position, rotation);
            var renderer = strip.GetComponent<Renderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }
}
