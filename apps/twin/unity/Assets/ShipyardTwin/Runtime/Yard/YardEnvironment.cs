using System.Collections.Generic;
using System.Linq;
using GrowthLab.Core;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Yard dimensions shared by the environment, crane and camera (metres, Unity axes). Docks come from the ERP
    /// facility footprints (yard plan); without a plan the placement dock falls back to the hull size.
    /// </summary>
    public static class YardLayout
    {
        /// <summary>Keel-block height: the dock floor sits this far below the ship baseline (y = 0).</summary>
        public const float KeelBlockHeight = 1.8f;

        public const float DockFloor = -KeelBlockHeight;
        public const float QuayLevel = 12f;
        public const float WallThickness = 6f;
        public const float SeaLevel = 6f;

        /// <summary>The facility the hull sits in (placement.dock), if the yard plan has it with a footprint.</summary>
        public static Facility PlacementDock(TwinFile twin) =>
            twin.yard?.facilities?.FirstOrDefault(f => f.location == twin.placement.dock && f.HasFootprint);

        /// <summary>Half length of the dock pit (inside the walls).</summary>
        public static float PitHalfLength(TwinFile twin)
        {
            var dock = PlacementDock(twin);
            return dock != null ? dock.size_x_m / 2f - WallThickness : twin.hull.length_m / 2f + 32f;
        }

        /// <summary>Half width of the dock pit (inside the walls).</summary>
        public static float PitHalfWidth(TwinFile twin)
        {
            var dock = PlacementDock(twin);
            return dock != null ? dock.size_y_m / 2f - WallThickness : 44f;
        }

        /// <summary>Dock centre in Unity: the yard plan footprint, else dock_position, else the hull's midship.</summary>
        public static Vector3 DockCentre(TwinFile twin)
        {
            var facility = PlacementDock(twin);
            if (facility != null)
            {
                return ShipCoordinates.YardToUnity(facility.x, facility.y, 0f);
            }

            var dock = twin.placement.dock_position;
            return dock != null && !string.IsNullOrEmpty(dock.location)
                ? ShipCoordinates.YardToUnity(dock.x, dock.y, 0f)
                : ShipCoordinates.YardToUnity(twin.placement.origin_x + twin.hull.length_m / 2f, twin.placement.origin_y, 0f);
        }

        /// <summary>Docks with footprints; the placement dock is synthesised from the hull when the plan lacks it.</summary>
        public static List<Facility> Docks(TwinFile twin)
        {
            var docks = (twin.yard?.facilities ?? new Facility[0]).Where(f => f.kind == "도크" && f.HasFootprint).ToList();
            if (PlacementDock(twin) == null)
            {
                var centre = DockCentre(twin);
                docks.Add(new Facility
                {
                    location = twin.placement.dock, kind = "도크", name = twin.placement.dock, x = centre.x, y = centre.z,
                    size_x_m = (twin.hull.length_m / 2f + 32f + WallThickness) * 2f, size_y_m = (44f + WallThickness) * 2f,
                });
            }

            return docks;
        }

        /// <summary>Yard x of the shoreline: the seaward end (gate) of the furthest dock.</summary>
        public static float Shoreline(TwinFile twin) => Docks(twin).Max(d => d.x + d.size_x_m / 2f);

        /// <summary>Margin of the paved site around the land footprints (m).</summary>
        public const float SiteMargin = 28f;

        /// <summary>
        /// Footprints the paved site is built from: docks, shops, yards, and a 1 m strip at the root of every quay
        /// (pier) that starts at the shoreline, so the paving runs out to the pier deck instead of stopping short of
        /// it with water in between. Mirrored in pipeline/generate_scenery.py (land_footprints).
        /// </summary>
        public static List<Facility> LandFootprints(TwinFile twin)
        {
            var docks = Docks(twin);
            var shore = Shoreline(twin);
            var land = twin.yard.facilities.Where(f => f.HasFootprint && f.kind != "안벽" && f.kind != "도크").Concat(docks).ToList();
            foreach (var quay in twin.yard.facilities.Where(f => f.HasFootprint && f.kind == "안벽"))
            {
                if (Mathf.Abs(quay.x - quay.size_x_m / 2f - shore) <= SiteMargin)
                {
                    land.Add(new Facility
                    {
                        location = quay.location + "-ROOT", kind = "안벽", name = quay.name, x = shore - 0.5f, y = quay.y,
                        size_x_m = 1f, size_y_m = quay.size_y_m,
                    });
                }
            }

            return land;
        }
    }

    /// <summary>
    /// Builds the yard from the exported ERP yard plan: every dock as a pit with walls, working altars and a gate;
    /// keel and side blocks under the docked hull; shops and warehouses as halls; yards as paved areas with staged
    /// blocks; fitting-out quays as jetties; quay ground around the pits and the sea beyond the shoreline.
    /// Also sets sunlight shadows, trilight ambient and distance fog.
    /// </summary>
    public sealed class YardEnvironment : MonoBehaviour, ICoreConsumer
    {
        [SerializeField] private Color _fogColor = new Color(0.74f, 0.80f, 0.86f);

        private ShipPalette _palette;

        public void Construct(CoreContext context)
        {
            if (_palette != null || !context.Services.TryGet(out _palette) || !context.Services.TryGet<TwinFile>(out var twin))
            {
                return;
            }

            Lighting();
            transform.position = Vector3.zero;
            var docks = YardLayout.Docks(twin);
            foreach (var dock in docks)
            {
                BuildDock(dock);
            }

            if (context.Services.TryGet<YardDataSource>(out var source) && source.Model.Hulls.Count > 0)
            {
                foreach (var hull in source.Model.Hulls)
                {
                    BuildSupports(hull.Info, hull.Placement);
                }
            }
            else
            {
                BuildSupports(twin.hull, twin.placement);
            }

            BuildGround(twin, docks);
            foreach (var facility in twin.yard.facilities.Where(f => f.HasFootprint && f.kind != "도크"))
            {
                BuildFacility(facility);
            }
        }

        /// <summary>
        /// Sky, ambient, reflections, shadows and post-processing come from the URP setup baked into the scene
        /// (Editor/ShipyardTwinRendering). Here: distance fog matched to the sky horizon and a refreshed ambient probe.
        /// </summary>
        private void Lighting()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = _fogColor;
            RenderSettings.fogStartDistance = 1200f;
            RenderSettings.fogEndDistance = 7000f;
            var camera = Camera.main;
            if (camera != null)
            {
                camera.farClipPlane = 9000f;
                if (RenderSettings.skybox == null)
                {
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = _fogColor;
                }
            }

            DynamicGI.UpdateEnvironment();
        }

        private void BuildDock(Facility dock)
        {
            var root = new GameObject(dock.location + " " + dock.name).transform;
            root.SetParent(transform, false);
            void Box(string name, Vector3 position, Vector3 size, string paint) => this.Box(name, root, position, size, paint, true);   // world position
            var c = ShipCoordinates.YardToUnity(dock.x, dock.y, 0f);
            var wall = YardLayout.WallThickness;
            var halfL = dock.size_x_m / 2f - wall;
            var halfW = dock.size_y_m / 2f - wall;
            float floor = YardLayout.DockFloor, quay = YardLayout.QuayLevel;
            Box("DockFloor", c + new Vector3(0, floor - 1f, 0), new Vector3(halfL * 2 + 2 * wall, 2f, halfW * 2 + 2 * wall), "dock_floor");
            foreach (var side in new[] { -1f, 1f })
            {
                Box("DockWall", c + new Vector3(0, (floor + quay) / 2, side * (halfW + wall / 2)), new Vector3(halfL * 2 + 2 * wall, quay - floor, wall), "concrete");
                Box("DockAltar", c + new Vector3(0, floor + 3f, side * (halfW - 1.5f)), new Vector3(halfL * 2, 6f, 3f), "concrete");
                Box("DockEdge", c + new Vector3(0, quay + 0.15f, side * (halfW + 0.3f)), new Vector3(halfL * 2, 0.3f, 0.6f), "marking");
            }

            Box("DockHead", c + new Vector3(-halfL - wall / 2, (floor + quay) / 2, 0), new Vector3(wall, quay - floor, halfW * 2), "concrete");
            Box("DockGate", c + new Vector3(halfL + 2f, (floor + quay) / 2, 0), new Vector3(4f, quay - floor + 1f, halfW * 2 + 2f), "crane_dark");
            Box("GateBand", c + new Vector3(halfL - 0.05f, quay - 2f, 0), new Vector3(0.2f, 1.2f, halfW * 2), "crane_red");
            HoverTarget.Attach(root.gameObject, "시설", HoverTarget.FacilityTitle(dock), FacilityDetail(dock));
        }

        private static string FacilityDetail(Facility f) =>
            (string.IsNullOrEmpty(f.kind) ? string.Empty : f.kind + " · ") + f.location +
            (f.HasFootprint ? " · " + f.size_x_m.ToString("0") + " × " + f.size_y_m.ToString("0") + " m" : string.Empty);

        private void BuildSupports(HullInfo info, HullPlacement placement)
        {
            var pose = ShipCoordinates.HullPose(placement);
            var root = new GameObject("KeelBlocks " + info.hull).transform;
            root.SetParent(transform, false);
            root.SetPositionAndRotation(pose.position, pose.rotation);
            var length = info.length_m;
            var h = YardLayout.KeelBlockHeight;
            // Keel blocks under the centre line; side (bilge) blocks under the flat bottom of the parallel body.
            for (var x = length * 0.06f; x < length * 0.96f; x += 2.4f)
            {
                Box("Keel", root, new Vector3(x, -h / 2, 0), new Vector3(1.4f, h, 2.2f), "keel_block");
            }

            var side = info.beam_m / 2f - 6f;
            for (var x = length * 0.10f; x < length * 0.86f; x += 6f)
            {
                foreach (var s in new[] { -1f, 1f })
                {
                    Box("Side", root, new Vector3(x, -h / 2, s * side), new Vector3(1.4f, h, 1.6f), "keel_block");
                }
            }

            ShipView.AttachShipTarget(root.gameObject, info);
        }

        /// <summary>
        /// Quay ground as strips around the dock pits (pits run to the shoreline), then the sea seaward of it.
        /// With satellite imagery and a geo-referenced site, the ground is the paved site only: the hull around the
        /// land facilities (grown by 28 m, cut at the shoreline) with a revetment down to the sea, so the imagery
        /// shows the real surroundings. Footprints are taken as aligned with the yard axes.
        /// </summary>
        private void BuildGround(TwinFile twin, List<Facility> docks)
        {
            var shore = YardLayout.Shoreline(twin);
            var land = YardLayout.LandFootprints(twin);
            var satellite = twin.yard.site != null && !string.IsNullOrEmpty(twin.yard.site.id) &&
                            Resources.Load<TextAsset>(SatelliteGround.Folder + "satellite") != null;
            var apron = satellite ? SiteOutline(land, shore) : null;
            var x0 = (apron?.Min(v => v.x) ?? land.Min(f => f.x - f.size_x_m / 2f) - 250f) - 1f;
            var y0 = (apron?.Min(v => v.y) ?? land.Min(f => f.y - f.size_y_m / 2f) - 250f) - 1f;
            var y1 = (apron?.Max(v => v.y) ?? land.Max(f => f.y + f.size_y_m / 2f) + 250f) + 1f;
            var cursor = y0;
            foreach (var dock in docks.OrderBy(d => d.y))
            {
                float dy0 = dock.y - dock.size_y_m / 2f, dy1 = dock.y + dock.size_y_m / 2f;
                float dx0 = dock.x - dock.size_x_m / 2f, dx1 = dock.x + dock.size_x_m / 2f;
                Ground(x0, shore, cursor, dy0, apron);
                Ground(x0, dx0, dy0, dy1, apron);
                Ground(dx1, shore, dy0, dy1, apron);
                cursor = Mathf.Max(cursor, dy1);
            }

            Ground(x0, shore, cursor, y1, apron);
            Sea(docks, shore);
            if (apron != null)
            {
                Revetment(apron, docks, shore, TerrainModel.Load(), twin.yard.site, SatelliteGround.DredgeBand(twin, apron));
                return;
            }

            // No imagery: land beyond the plan fades into the fog as a wide apron behind the yard.
            const float depth = 26f;
            HoverTarget.Occluder(Box("Hinterland", new Vector3(x0 - 1500f, YardLayout.QuayLevel - depth / 2, (y0 + y1) / 2f),
                new Vector3(3000f, depth, (y1 - y0) + 4000f), "hinterland"));
        }

        /// <summary>
        /// Sea surface everywhere around the site (bays inland of the shoreline included, seen through the satellite
        /// imagery's transparent water) except over the dock pits, which are dry below sea level.
        /// </summary>
        private void Sea(List<Facility> docks, float shore)
        {
            const float reach = 6000f;
            float cx = docks.Average(d => d.x), cy = docks.Average(d => d.y);
            float x0 = cx - reach, x1 = cx + reach, y = cy - reach;
            var pieces = new List<List<Vector2>>();
            foreach (var dock in docks.OrderBy(d => d.y))
            {
                float dy0 = dock.y - dock.size_y_m / 2f, dy1 = dock.y + dock.size_y_m / 2f;
                pieces.Add(Polygon2D.Rect(x0, x1, y, dy0));
                pieces.Add(Polygon2D.Rect(x0, dock.x - dock.size_x_m / 2f, dy0, dy1));
                pieces.Add(Polygon2D.Rect(dock.x + dock.size_x_m / 2f + 4f, x1, dy0, dy1));   // beyond the gate
                y = Mathf.Max(y, dy1);
            }

            pieces.Add(Polygon2D.Rect(x0, x1, y, cy + reach));
            foreach (var piece in pieces.Where(r => r[1].x - r[0].x > 0.5f && r[2].y - r[1].y > 0.5f))
            {
                var go = new GameObject("Sea");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = Polygon2D.Flat(piece, YardLayout.SeaLevel);
                go.AddComponent<MeshRenderer>().sharedMaterial = _palette.Get("water");
                HoverTarget.Occluder(go);
            }
        }

        /// <summary>Paved site outline of the yard plan (<see cref="YardLayout.LandFootprints"/>).</summary>
        public static List<Vector2> SiteOutline(TwinFile twin) => SiteOutline(YardLayout.LandFootprints(twin), YardLayout.Shoreline(twin));

        /// <summary>Paved site outline: convex hull of land footprints grown by 28 m, cut at the shoreline.</summary>
        public static List<Vector2> SiteOutline(IEnumerable<Facility> land, float shore, float margin = YardLayout.SiteMargin)
        {
            var corners = land.SelectMany(f => Polygon2D.Rect(f.x - f.size_x_m / 2f - margin, f.x + f.size_x_m / 2f + margin,
                f.y - f.size_y_m / 2f - margin, f.y + f.size_y_m / 2f + margin));
            return Polygon2D.ClipConvex(Polygon2D.ConvexHull(corners), Polygon2D.Rect(-1e5f, shore, -1e5f, 1e5f));
        }

        private void Ground(float x0, float x1, float y0, float y1, List<Vector2> apron)
        {
            if (x1 - x0 < 0.5f || y1 - y0 < 0.5f)
            {
                return;
            }

            if (apron == null)
            {
                const float depth = 26f;
                HoverTarget.Occluder(Box("Quay", new Vector3((x0 + x1) / 2f, YardLayout.QuayLevel - depth / 2, (y0 + y1) / 2f), new Vector3(x1 - x0, depth, y1 - y0), "concrete"));
                return;
            }

            var piece = Polygon2D.ClipConvex(Polygon2D.Rect(x0, x1, y0, y1), apron);
            if (piece.Count < 3)
            {
                return;
            }

            var go = new GameObject("Quay");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = Polygon2D.Flat(piece, YardLayout.QuayLevel);
            go.AddComponent<MeshRenderer>().sharedMaterial = _palette.Get("concrete");
            HoverTarget.Occluder(go);
        }

        /// <summary>
        /// Quay wall from quay level to below the sea along the shoreline (berths), leaving the dock gates open; other
        /// sea-facing edges (outside is water in the terrain) get a sloped rubble mound; land-facing edges get graded terrain.
        /// </summary>
        private void Revetment(List<Vector2> outline, List<Facility> docks, float shore, TerrainModel terrain, YardSite site, (float, float) band)
        {
            bool Seaward(Vector2 p, Vector2 q) => IsSeawardEdge(p, q, terrain, site, band, shore);

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            void Wall(Vector2 p, Vector2 q)
            {
                var start = vertices.Count;
                foreach (var (v, h) in new[] { (p, YardLayout.QuayLevel), (q, YardLayout.QuayLevel), (q, YardLayout.SeaLevel - 3f), (p, YardLayout.SeaLevel - 3f) })
                {
                    vertices.Add(ShipCoordinates.YardToUnity(v.x, v.y, h));
                }

                triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3, start, start + 2, start + 1, start, start + 3, start + 2 });
            }

            var mound = new List<(Vector2 P, Vector2 Q)>();
            for (var i = 0; i < outline.Count; i++)
            {
                Vector2 p = outline[i], q = outline[(i + 1) % outline.Count];
                if (Mathf.Abs(p.x - shore) > 0.01f || Mathf.Abs(q.x - shore) > 0.01f)
                {
                    if (Seaward(p, q))
                    {
                        mound.Add((p, q));
                    }

                    continue;
                }

                // Shoreline edge: wall only between the dock gates.
                float lo = Mathf.Min(p.y, q.y), hi = Mathf.Max(p.y, q.y), y = lo;
                foreach (var dock in docks.OrderBy(d => d.y))
                {
                    float g0 = dock.y - dock.size_y_m / 2f, g1 = dock.y + dock.size_y_m / 2f;
                    if (g0 > y)
                    {
                        Wall(new Vector2(shore, y), new Vector2(shore, Mathf.Min(g0, hi)));
                    }

                    y = Mathf.Max(y, g1);
                }

                if (hi > y)
                {
                    Wall(new Vector2(shore, y), new Vector2(shore, hi));
                }
            }

            // Where the vertical quay wall meets a sloped mound at a shoreline corner, close the wedge between the
            // wall's end and the slope's side so no gap shows under the paving's corner.
            float toe = YardLayout.SeaLevel - 4f, run = (YardLayout.QuayLevel - toe) * 1.5f;
            foreach (var (p, q) in mound)
            {
                var outward = new Vector2(q.y - p.y, p.x - q.x).normalized * run;
                foreach (var corner in new[] { p, q }.Where(c => Mathf.Abs(c.x - shore) < 0.01f))
                {
                    var start = vertices.Count;
                    vertices.Add(ShipCoordinates.YardToUnity(corner.x, corner.y, YardLayout.QuayLevel));
                    vertices.Add(ShipCoordinates.YardToUnity(corner.x, corner.y, YardLayout.SeaLevel - 3f));
                    var foot = corner + outward;                                   // the slope's toe at this corner
                    vertices.Add(ShipCoordinates.YardToUnity(foot.x, foot.y, toe));
                    triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 1 });
                }
            }

            var mesh = new Mesh { name = "Revetment" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetUVs(0, ObjMeshParser.BoxUvs(vertices, new List<List<int>> { triangles }));
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject("Revetment");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _palette.Get("wall_base");
            HoverTarget.Occluder(go);
            RubbleMound(mound);
        }

        /// <summary>
        /// True when a site outline edge (counter-clockwise) faces water: it gets a rubble mound instead of graded ground.
        /// Shared with SatelliteGround so the terrain under the paving matches the revetment.
        /// </summary>
        public static bool IsSeawardEdge(Vector2 p, Vector2 q, TerrainModel terrain, YardSite site, (float, float) band, float shore)
        {
            if (terrain == null)
            {
                return true;
            }

            var mid = (p + q) / 2f;
            var outward = new Vector2(q.y - p.y, p.x - q.x).normalized;     // right of a counter-clockwise edge
            // Water anywhere 20-60 m out (the terrain grid is ~16 m, so one sample can land on a beach cell).
            return new[] { 20f, 40f, 60f }.Select(d => mid + outward * d)
                .Any(at => SatelliteGround.Dredged(band, shore, at, terrain.AtYard(site, at.x, at.y)) < 0.5f);
        }

        /// <summary>
        /// Sloped rubble-mound revetment (about 1:1.5, the usual Korean reclamation edge) from quay level to below the
        /// sea on the given edges, with boulders on the face and concrete tetrapods along the waterline.
        /// </summary>
        private void RubbleMound(List<(Vector2 P, Vector2 Q)> edges)
        {
            if (edges.Count == 0)
            {
                return;
            }

            float top = YardLayout.QuayLevel, toe = YardLayout.SeaLevel - 4f, run = (top - toe) * 1.5f;
            var vertices = new List<Vector3>();
            void Tri(Vector3 a, Vector3 b, Vector3 c)
            {
                if (Vector3.Cross(b - a, c - a).y < 0f)
                {
                    (b, c) = (c, b);
                }

                vertices.Add(a);
                vertices.Add(b);
                vertices.Add(c);
            }

            Vector2 Outward(Vector2 p, Vector2 q) => new Vector2(q.y - p.y, p.x - q.x).normalized;
            Vector3 At(Vector2 v, float h) => ShipCoordinates.YardToUnity(v.x, v.y, h);
            var random = new System.Random(5);
            var rocks = new[] { "rock_a", "rock_b", "rock_c" }.ToDictionary(m => m, m => new List<Matrix4x4>());
            var tetrapods = new List<Matrix4x4>();
            float Next(float lo, float hi) => lo + (float)random.NextDouble() * (hi - lo);
            foreach (var (p, q) in edges)
            {
                var o = Outward(p, q) * run;
                Tri(At(p, top), At(q, top), At(q + o, toe));
                Tri(At(p, top), At(q + o, toe), At(p + o, toe));
                // Fill the wedge to the next sloped edge at a convex corner.
                foreach (var (r, s) in edges.Where(e => (e.P - q).sqrMagnitude < 0.01f))
                {
                    Tri(At(q, top), At(q + o, toe), At(q + Outward(r, s) * run, toe));
                }

                var length = Vector2.Distance(p, q);
                var along = (q - p) / length;
                var outward = o / run;
                for (var d = 2.5f; d < length - 2.5f; d += 3.4f)
                {
                    // Tetrapods (about 5 m, the 16-25 t class) in three staggered rows from just above the waterline
                    // to the toe; boulders scattered on the face above them.
                    var r = 0;
                    foreach (var row in new[] { 0.5f, 0.66f, 0.82f })
                    {
                        var at = p + along * (d + Next(-0.5f, 0.5f) + (r++ % 2) * 1.7f) + outward * (run * row + Next(-0.8f, 0.8f));
                        var h = Mathf.Lerp(top, toe, row) - 1.2f;
                        tetrapods.Add(Matrix4x4.TRS(At(at, h), Quaternion.Euler(Next(0, 360), Next(0, 360), Next(0, 360)), Vector3.one * Next(1.35f, 1.6f)));
                    }

                    for (var b = 0; b < 2; b++)
                    {
                        var t = Next(0.05f, 0.42f);
                        var at = p + along * (d + Next(-1.7f, 1.7f)) + outward * run * t;
                        var size = Next(1.2f, 2.8f);
                        rocks.ElementAt(random.Next(3)).Value.Add(Matrix4x4.TRS(At(at, Mathf.Lerp(top, toe, t) - 0.35f * size),
                            Quaternion.Euler(Next(-15, 15), Next(0, 360), Next(-15, 15)), Vector3.one * size));
                    }
                }
            }

            var mesh = new Mesh { name = "RubbleMound" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(Enumerable.Range(0, vertices.Count).ToArray(), 0);
            mesh.SetUVs(0, ObjMeshParser.BoxUvs(vertices, new List<List<int>> { Enumerable.Range(0, vertices.Count).ToList() }));
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject("RubbleMound");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _palette.Get("riprap");
            HoverTarget.Occluder(go);
            PropLibrary.Combine("tetrapod", tetrapods, _palette, transform, "Armour");
            foreach (var pair in rocks)
            {
                PropLibrary.Combine(pair.Key, pair.Value, _palette, transform, "Armour");
            }
        }

        private void BuildFacility(Facility f)
        {
            var root = new GameObject(f.location + " " + f.name).transform;
            root.SetParent(transform, false);
            root.SetPositionAndRotation(ShipCoordinates.YardToUnity(f.x, f.y, YardLayout.QuayLevel), ShipCoordinates.HeadingToUnity(f.heading_deg));
            float sx = f.size_x_m, sy = f.size_y_m, h = Mathf.Max(f.size_z_m, 0f);
            switch (f.kind)
            {
                case "작업장":
                case "창고":
                    if (PropLibrary.TryCreate(f.location, _palette, root, out _))
                    {
                        break; // generated building sized to this facility (pipeline/generate_scenery.py)
                    }

                    h = h > 0 ? h : 20f;
                    Box("Hall", root, new Vector3(0, h / 2, 0), new Vector3(sx, h, sy), "shop_wall");
                    Box("Roof", root, new Vector3(0, h + 1f, 0), new Vector3(sx + 2f, 2f, sy + 2f), "shop_roof");
                    Box("Band", root, new Vector3(sx / 2f + 0.15f, h * 0.78f, 0), new Vector3(0.3f, h * 0.1f, sy), "shop_roof");
                    Box("Door", root, new Vector3(sx / 2f + 0.2f, h * 0.32f, 0), new Vector3(0.4f, h * 0.64f, Mathf.Min(sy * 0.45f, 40f)), "crane_dark");
                    break;
                case "야드":
                    Box("Pavement", root, new Vector3(0, 0.15f, 0), new Vector3(sx, 0.3f, sy), "asphalt");
                    var rows = Mathf.Max(1, Mathf.FloorToInt(sy / 28f));
                    var cols = Mathf.Max(1, Mathf.FloorToInt(sx / 32f));
                    for (var r = 0; r < rows; r++)
                    {
                        for (var c = 0; c < cols; c++)
                        {
                            if ((r * 7 + c * 3) % 5 == 0)
                            {
                                continue; // leave gaps for transporters
                            }

                            var size = new Vector3(12f + (c % 3) * 3f, 4f + ((r + c) % 3) * 2.5f, 10f + (r % 2) * 4f);
                            var x = -sx / 2f + (c + 0.5f) * sx / cols;
                            var z = -sy / 2f + (r + 0.5f) * sy / rows;
                            Box("StagedBlock", root, new Vector3(x, 0.3f + size.y / 2f, z), size, (r + c) % 4 == 0 ? "antifouling" : "primer");
                        }
                    }

                    break;
                case "안벽":
                    Box("Jetty", root, new Vector3(0, -10f, 0), new Vector3(sx, 20f, sy), "concrete");
                    Box("Fender", root, new Vector3(0, -2f, sy / 2f + 0.4f), new Vector3(sx, 2.5f, 0.8f), "crane_dark");
                    Box("Fender", root, new Vector3(0, -2f, -sy / 2f - 0.4f), new Vector3(sx, 2.5f, 0.8f), "crane_dark");
                    for (var x = -sx / 2f + 10f; x < sx / 2f; x += 25f)
                    {
                        foreach (var s in new[] { -1f, 1f })
                        {
                            Box("Bollard", root, new Vector3(x, 0.6f, s * (sy / 2f - 1.5f)), new Vector3(0.9f, 1.2f, 0.9f), "crane_dark");
                        }
                    }

                    break;
            }

            HoverTarget.Attach(root.gameObject, "시설", HoverTarget.FacilityTitle(f), FacilityDetail(f));
        }

        private GameObject Box(string name, Vector3 position, Vector3 size, string paint) => Box(name, transform, position, size, paint, true);

        private GameObject Box(string name, Transform parent, Vector3 localPosition, Vector3 size, string paint, bool world = false)
        {
            var box = MetricBox.Create(name, parent, size, _palette.Get(paint));
            if (world)
            {
                box.transform.position = localPosition;
            }
            else
            {
                box.transform.localPosition = localPosition;
                box.transform.localRotation = Quaternion.identity;
            }

            return box;
        }

        private void Update()
        {
            if (_palette != null)
            {
                // Slow drift of the ripple normal map.
                _palette.Get("water").SetTextureOffset("_BaseMap", new Vector2(Time.time * 0.004f, Time.time * 0.0025f));
            }
        }
    }
}
