using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace ShipyardTwin.Tests
{
    public sealed class ShipyardTwinTests
    {
        private const string Json = @"{
 ""format"": ""shipyard-twin/1"",
 ""hull"": {""hull"": ""T1"", ""length_m"": 100, ""beam_m"": 20, ""depth_m"": 10},
 ""placement"": {""dock"": ""D"", ""origin_x"": 50, ""origin_y"": 10, ""origin_z"": 0, ""heading_deg"": 90,
   ""dock_position"": {""location"": ""D"", ""x"": 100, ""y"": 10, ""z"": 0, ""heading_deg"": 0}},
 ""blocks"": [
  {""block"": ""T1-221C1"", ""zone"": ""z"", ""weight_t"": 100, ""step"": 2, ""predecessors"": [""T1-241C1""], ""forecast"": ""2027-03-04"",
   ""erected_day"": null, ""model_asset"": null, ""x_min"": 10, ""x_max"": 20, ""y_min"": -5, ""y_max"": 5, ""z_min"": 0, ""z_max"": 3,
   ""cog_x"": 15, ""cog_y"": 0, ""cog_z"": 1.5},
  {""block"": ""T1-241C1"", ""zone"": ""z"", ""weight_t"": 50, ""step"": 1, ""predecessors"": [], ""forecast"": ""2027-03-01"",
   ""erected_day"": null, ""model_asset"": null, ""x_min"": 0, ""x_max"": 10, ""y_min"": 0, ""y_max"": 8, ""z_min"": 0, ""z_max"": 2,
   ""cog_x"": 5, ""cog_y"": 4, ""cog_z"": 1},
  {""block"": ""T1-511C1"", ""zone"": ""z"", ""weight_t"": 10, ""step"": 3, ""predecessors"": [], ""forecast"": null,
   ""erected_day"": null, ""model_asset"": null, ""x_min"": 0, ""x_max"": 4, ""y_min"": -2, ""y_max"": 2, ""z_min"": 10, ""z_max"": 14,
   ""cog_x"": 2, ""cog_y"": 0, ""cog_z"": 12}
 ]}";

        [Test]
        public void TwinFile_ParsesExportAndRejectsOtherFormats()
        {
            var twin = TwinFile.Parse(Json);
            Assert.AreEqual("T1", twin.hull.hull);
            Assert.AreEqual(3, twin.blocks.Length);
            CollectionAssert.AreEqual(new[] { "T1-241C1" }, twin.blocks[0].predecessors);
            Assert.AreEqual("2027-03-04", twin.blocks[0].Day);
            Assert.AreEqual(100f, twin.placement.dock_position.x);
            Assert.Throws<FormatException>(() => TwinFile.Parse("{\"format\":\"other\"}"));
        }

        [Test]
        public void Coordinates_MapShipAndYardAxesWithoutMirroring()
        {
            Assert.AreEqual(new Vector3(1, 3, 2), ShipCoordinates.ShipToUnity(1, 2, 3));
            Assert.AreEqual(new Vector3(1, 2, -3), ShipCoordinates.ObjToUnity(1, 2, 3));
            // OBJ (ship x, ship z, -ship y) of ship point (1, 2, 3) lands where ShipToUnity puts it.
            Assert.AreEqual(ShipCoordinates.ShipToUnity(1, 2, 3), ShipCoordinates.ObjToUnity(1, 3, -2));
            // Heading 90° turns ship forward (yard +x) to yard +y, i.e. Unity +z.
            var forward = ShipCoordinates.HeadingToUnity(90f) * Vector3.right;
            Assert.That(Vector3.Distance(forward, Vector3.forward), Is.LessThan(1e-5f));
            var pose = ShipCoordinates.HullPose(TwinFile.Parse(Json).placement);
            Assert.AreEqual(new Vector3(50, 0, 10), pose.position);
            var bounds = ShipCoordinates.LocalBounds(TwinFile.Parse(Json).blocks[1]);
            Assert.AreEqual(new Vector3(-5, -1, -4), bounds.min);
            Assert.AreEqual(new Vector3(5, 1, 4), bounds.max);
        }

        [Test]
        public void ObjParser_ConvertsAxesFansPolygonsAndSplitsBackFaces()
        {
            const string obj = "# quad, both windings\nv 0 0 0\nv 1 0 0\nv 1 0 -1\nv 0 0 -1\nf 1 2 3 4\nf 1/1 4/4 3/3 2/2\n";
            var result = ObjMeshParser.Parse(obj);
            Assert.AreEqual(new Vector3(1, 0, 1), result.Vertices[2]);
            Assert.AreEqual(12, result.Triangles.Count);
            // Front face (reversed for Unity): 1-3-2 fan → indices 0,2,1 and 0,3,2.
            CollectionAssert.AreEqual(new[] { 0, 2, 1, 0, 3, 2 }, result.Triangles.Take(6));
            // Back face reuses no vertex of the front triangles it mirrors.
            Assert.AreEqual(4 + 6, result.Vertices.Count);
            var mesh = ObjMeshParser.ToMesh(result, "quad");
            var normals = mesh.normals;
            Assert.That(normals.All(n => n.sqrMagnitude > 0.9f), "No normal cancelled out.");
            UnityEngine.Object.DestroyImmediate(mesh);
            Assert.Throws<FormatException>(() => ObjMeshParser.Parse("v 0 0 0\nf 1 2 3\n"));
            Assert.AreEqual(3, ObjMeshParser.Parse("v 0 0 0\nv 1 0 0\nv 0 1 0\nf -3 -2 -1\n").Triangles.Count);
        }

        [Test]
        public void ObjParser_SplitsSubmeshesByMaterialInFirstUseOrder()
        {
            const string obj = "v 0 0 0\nv 1 0 0\nv 0 1 0\nv 0 0 1\nf 1 2 3\nusemtl deck\nf 1 2 4\nusemtl antifouling\nf 1 3 4\nusemtl deck\nf 2 3 4\n";
            var result = ObjMeshParser.Parse(obj);
            CollectionAssert.AreEqual(new[] { "", "deck", "antifouling" }, result.Materials);
            CollectionAssert.AreEqual(new[] { 3, 6, 3 }, result.Submeshes.Select(s => s.Count));
            var mesh = ObjMeshParser.ToMesh(result, "m");
            Assert.AreEqual(3, mesh.subMeshCount);
            UnityEngine.Object.DestroyImmediate(mesh);
        }

        [Test]
        public void Palette_KnowsGeneratorPaintsAndFallsBackToPrimer()
        {
            foreach (var name in new[] { "antifouling", "boottop", "topside", "deck", "primer", "glass", "bronze", "crane_white", "concrete" })
            {
                Assert.IsTrue(ShipPalette.Paints.ContainsKey(name), name);
            }

            Assert.AreEqual(ShipPalette.Paints["primer"].Color, ShipPalette.Lookup("no-such-paint").Color);
            var palette = new ShipPalette(null);
            var deck = palette.Get("deck");
            Assert.AreSame(deck, palette.Get("deck"), "One shared material per paint.");
            Assert.AreEqual(ShipPalette.Paints["deck"].Color, deck.color);
            Assert.AreSame(palette.Get("primer"), palette.Get("unknown"));
            UnityEngine.Object.DestroyImmediate(deck);
        }

        [Test]
        public void ObjParser_KeepsNormalsAndUvsFromBlenderStyleFiles()
        {
            // A cube corner: two faces share position 1 but have different normals and UVs.
            const string obj = "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nv 0 0 -1\nv 0 1 -1\n" +
                               "vt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\nvn 0 0 1\nvn -1 0 0\n" +
                               "usemtl wall_panel\nf 1/1/1 2/2/1 3/3/1 4/4/1\nf 1/2/2 4/3/2 6/4/2 5/1/2\n";
            var result = ObjMeshParser.Parse(obj);
            Assert.AreEqual(8, result.Vertices.Count, "Position 1 and 4 split by normal.");
            Assert.AreEqual(12, result.Triangles.Count);
            Assert.AreEqual(new Vector3(0, 0, -1), result.Normals[0], "OBJ +z normal becomes Unity -z.");
            Assert.AreEqual(new Vector2(0, 0), result.Uvs[0]);
            var mesh = ObjMeshParser.ToMesh(result, "corner");
            Assert.AreEqual(8, mesh.uv.Length);
            UnityEngine.Object.DestroyImmediate(mesh);

            var box = ObjMeshParser.BoxUvs(new System.Collections.Generic.List<Vector3> { new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(0, 0, 3) },
                new System.Collections.Generic.List<System.Collections.Generic.List<int>> { new System.Collections.Generic.List<int> { 0, 1, 2 } });
            Assert.AreEqual(new Vector2(4, 0), box[1], "Floor triangle: UV = (x, z) in metres.");
            Assert.AreEqual(new Vector2(0, 3), box[2]);
        }

        [Test]
        public void Schedule_OrdersByDayLiftsThenLands()
        {
            var schedule = new ErectionSchedule(TwinFile.Parse(Json).blocks, liftDays: 2f);
            CollectionAssert.AreEqual(new[] { "T1-241C1", "T1-221C1", "T1-511C1" }, schedule.Entries.Select(e => e.Block.block));
            Assert.AreEqual(new DateTime(2027, 2, 27), schedule.Start);
            // 241C1 lands day 2 (03-01), 221C1 day 5 (03-04), undated 511C1 one lift after the last: day 7.
            CollectionAssert.AreEqual(new[] { 2f, 5f, 7f }, schedule.Entries.Select(e => e.FinishDay));
            Assert.AreEqual(7f, schedule.Duration);

            var first = schedule.Entries[0];
            Assert.AreEqual(0f, schedule.Progress(first));
            var landed = schedule.Seek(1f);
            Assert.AreEqual(0.5f, schedule.Progress(first), 1e-5f);
            Assert.AreEqual(0, landed.Count);
            Assert.AreEqual(1, schedule.CurrentStep);
            Assert.AreEqual("T1-241C1", schedule.Lifting().Single().Block.block);

            landed = schedule.Advance(4.5f);
            CollectionAssert.AreEqual(new[] { "T1-241C1", "T1-221C1" }, landed.Select(e => e.Block.block));
            Assert.AreEqual(150f, schedule.ErectedWeight);
            Assert.AreEqual(3, schedule.CurrentStep, "511C1 has started lifting.");
            Assert.AreEqual(new DateTime(2027, 3, 4, 12, 0, 0), schedule.CurrentDate);

            Assert.AreEqual(0, schedule.Seek(0f).Count, "Seeking backwards erects nothing.");
            Assert.AreEqual(0, schedule.ErectedCount);
            schedule.Seek(100f);
            Assert.IsTrue(schedule.Finished);
            Assert.AreEqual(7f, schedule.Cursor);
        }

        [Test]
        public void Schedule_LiftsSameDayBlocksOneAtATimeBeforeTheirDay()
        {
            BlockRecord Block(string id, int step, string day) => new BlockRecord { block = id, step = step, forecast = day };
            var schedule = new ErectionSchedule(new[]
            {
                Block("K", 1, "2027-03-01"), Block("C", 2, "2027-03-04"), Block("A", 2, "2027-03-04"), Block("B", 3, "2027-03-04"),
            }, liftDays: 2f);

            // Key block day 2; the three 03-04 blocks share days 2..5 in step/id order: A, C (step 2), then B.
            CollectionAssert.AreEqual(new[] { "K", "A", "C", "B" }, schedule.Entries.Select(e => e.Block.block));
            CollectionAssert.AreEqual(new[] { 2f, 3f, 4f, 5f }, schedule.Entries.Select(e => e.FinishDay));
            CollectionAssert.AreEqual(new[] { 2f, 1f, 1f, 1f }, schedule.Entries.Select(e => e.LiftDays));
            for (var cursor = 0f; cursor <= 5f; cursor += 0.25f)
            {
                schedule.Seek(cursor);
                Assert.LessOrEqual(schedule.Lifting().Count(), 1, "One crane lifts one block at a time (day " + cursor + ").");
            }
        }

        [Test]
        public void Playback_TicksAtSpeedPublishesLandingsAndStopsAtEnd()
        {
            var playback = new ErectionPlayback(new ErectionSchedule(TwinFile.Parse(Json).blocks, 2f), daysPerSecond: 1f, playing: true);
            var landed = new List<string>();
            playback.BlockErected += e => landed.Add(e.Block.block);

            playback.Tick(2f);
            CollectionAssert.AreEqual(new[] { "T1-241C1" }, landed);
            Assert.AreEqual(2f, playback.NextSpeed());
            playback.Tick(2.5f);
            Assert.AreEqual(3, landed.Count);
            Assert.IsFalse(playback.Playing, "Stops when finished.");
            playback.Playing = false;
            playback.Tick(10f);
            Assert.AreEqual(3, landed.Count);

            playback.Restart();
            Assert.IsTrue(playback.Playing);
            Assert.AreEqual(0f, playback.Schedule.Cursor);
            Assert.Throws<ArgumentOutOfRangeException>(() => new ErectionSchedule(new BlockRecord[0], 0f));
        }

        [Test]
        public void YardLayout_UsesErpDockFootprintAndFallsBackToHull()
        {
            var twin = TwinFile.Parse(Json);
            Assert.AreEqual(new Vector3(100, 0, 10), YardLayout.DockCentre(twin), "No plan: dock_position.");
            Assert.AreEqual(82f, YardLayout.PitHalfLength(twin), "No plan: hull length / 2 + 32.");
            Assert.AreEqual(1, YardLayout.Docks(twin).Count, "Placement dock synthesised.");

            twin.yard = new YardPlan
            {
                site = new YardSite { id = "S", name = "가상", lat = 34.83, lon = 128.55, x_axis_bearing_deg = 180 },
                facilities = new[]
                {
                    new Facility { location = "D", kind = "도크", x = 120, y = 20, size_x_m = 200, size_y_m = 60 },
                    new Facility { location = "D2", kind = "도크", x = 100, y = 120, size_x_m = 300, size_y_m = 60 },
                    new Facility { location = "P", kind = "작업장", x = -50, y = 0, size_x_m = 40, size_y_m = 20, size_z_m = 15 },
                    new Facility { location = "Q", kind = "안벽" },
                },
            };
            Assert.AreEqual(new Vector3(120, 0, 20), YardLayout.DockCentre(twin));
            Assert.AreEqual(94f, YardLayout.PitHalfLength(twin));
            Assert.AreEqual(24f, YardLayout.PitHalfWidth(twin));
            Assert.AreEqual(2, YardLayout.Docks(twin).Count);
            Assert.AreEqual(250f, YardLayout.Shoreline(twin), "Seaward end of the longest-reaching dock.");
            Assert.IsFalse(twin.yard.facilities[3].HasFootprint);
        }

        [Test]
        public void Scenery_ParsesFlatRoutesAndRouteMathLoopsOrPingPongs()
        {
            var scenery = SceneryFile.Parse(@"{""format"":""shipyard-scenery/1"",""roads"":[{""x0"":0,""y0"":0,""x1"":10,""y1"":0,""width"":8}],
                ""props"":[{""model"":""car_red"",""x"":1,""y"":2,""heading"":90}],
                ""routes"":[{""model"":""truck"",""speed"":2,""loop"":true,""path"":[0,0,10,0,10,10,0,10]}]}");
            Assert.AreEqual(8f, scenery.roads[0].width);
            Assert.AreEqual(90f, scenery.props[0].heading);
            var square = scenery.routes[0].Points();
            CollectionAssert.AreEqual(new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10) }, square);
            Assert.AreEqual(40f, RouteMath.Length(square, loop: true));
            var (p, heading) = RouteMath.At(square, true, 15f);
            Assert.AreEqual(new Vector2(10, 5), p);
            Assert.AreEqual(90f, heading, 1e-4f, "Driving +y on the second side.");
            Assert.AreEqual(new Vector2(5, 0), RouteMath.At(square, true, 45f).Position, "Loops wrap.");

            var line = new[] { new Vector2(0, 0), new Vector2(10, 0) };
            var (back, backHeading) = RouteMath.At(line, false, 13f);
            Assert.AreEqual(new Vector2(7, 0), back, "Open paths drive back.");
            Assert.AreEqual(180f, backHeading, 1e-4f);
            Assert.Throws<FormatException>(() => SceneryFile.Parse("{}"));
        }

        [Test]
        public void Geo_RoundTripsYardAndWebMercator()
        {
            var site = new YardSite { id = "S", lat = 34.833114, lon = 128.59749, x_axis_bearing_deg = 235 };
            var (lat, lon) = ShipCoordinates.YardToGeo(site, 430f, 170f);
            var back = ShipCoordinates.GeoToYard(site, lat, lon);
            Assert.That(Vector2.Distance(back, new Vector2(430f, 170f)), Is.LessThan(0.01f));
            // Bearing 235: yard +x points south-west, so +x lowers latitude and longitude.
            var (lat2, lon2) = ShipCoordinates.YardToGeo(site, 100f, 0f);
            Assert.Less(lat2, site.lat);
            Assert.Less(lon2, site.lon);
            // Web Mercator: tile (0,0) corner at zoom 0 is (85.05 N, 180 W); the centre is (0, 0).
            var (cLat, cLon) = ShipCoordinates.MercatorPixelToGeo(0, 128, 128);
            Assert.AreEqual(0.0, cLat, 1e-9);
            Assert.AreEqual(0.0, cLon, 1e-9);
            Assert.AreEqual(85.0511, ShipCoordinates.MercatorPixelToGeo(0, 0, 0).Lat, 1e-3);
        }

        [Test]
        public void Polygon_HullClipAndSiteOutline()
        {
            var hull = Polygon2D.ConvexHull(new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10), new Vector2(5, 5) });
            Assert.AreEqual(4, hull.Count, "Interior point dropped.");
            var clipped = Polygon2D.ClipConvex(Polygon2D.Rect(5, 20, -5, 5), hull);
            Assert.AreEqual(4, clipped.Count);
            Assert.AreEqual(25f, Area(clipped), 1e-3f);
            var outline = YardEnvironment.SiteOutline(new[] { new Facility { x = 0, y = 0, size_x_m = 100, size_y_m = 50 } }, shore: 40f, margin: 10f);
            Assert.AreEqual(40f, outline.Max(v => v.x), 1e-4f, "Cut at the shoreline.");
            Assert.AreEqual(-60f, outline.Min(v => v.x), 1e-4f);
            var flat = Polygon2D.Flat(outline, 12f);
            Assert.Greater(flat.normals[0].y, 0.99f, "Ground faces up.");
            Assert.IsTrue(Polygon2D.Contains(outline, new Vector2(0, 0)));
            Assert.IsFalse(Polygon2D.Contains(outline, new Vector2(45, 0)), "Seaward of the shoreline.");
            Assert.AreEqual(0f, Polygon2D.Distance(outline, new Vector2(0, 0)));
            Assert.AreEqual(5f, Polygon2D.Distance(outline, new Vector2(45, 0)), 1e-4f);
            UnityEngine.Object.DestroyImmediate(flat);
        }

        [Test]
        public void SiteOutline_ReachesThePierRootAndJunctionsAreDetected()
        {
            var twin = TwinFile.Parse(Json);
            twin.yard = new YardPlan
            {
                site = new YardSite { id = "S", lat = 34.83, lon = 128.55, x_axis_bearing_deg = 180 },
                facilities = new[]
                {
                    new Facility { location = "D", kind = "도크", x = 430, y = 10, size_x_m = 370, size_y_m = 100 },
                    new Facility { location = "W", kind = "작업장", x = 470, y = -150, size_x_m = 120, size_y_m = 60 },
                    new Facility { location = "P", kind = "안벽", x = 935, y = -150, size_x_m = 640, size_y_m = 40 },
                },
            };
            var shore = YardLayout.Shoreline(twin);
            Assert.AreEqual(615f, shore);
            var withRoot = YardEnvironment.SiteOutline(twin);
            var withoutRoot = YardEnvironment.SiteOutline(twin.yard.facilities.Where(f => f.kind != "안벽"), shore);
            foreach (var y in new[] { -170f, -150f, -130f })
            {
                Assert.IsTrue(Polygon2D.Contains(withRoot, new Vector2(shore - 0.5f, y)), "Paving meets the pier deck at y " + y);
            }

            Assert.IsFalse(Polygon2D.Contains(withoutRoot, new Vector2(shore - 0.5f, -150f)), "Without the root the paving stops short.");
            Assert.LessOrEqual(withRoot.Max(v => v.x), shore + 1e-3f, "Never past the shoreline (berths and dock gate stay open).");
            Assert.AreEqual(3, YardLayout.LandFootprints(twin).Count, "Dock, shop and the pier root strip.");

            var main = new Road { x0 = 0, y0 = 0, x1 = 100, y1 = 0, width = 14 };
            var side = new Road { x0 = 50, y0 = 0, x1 = 50, y1 = 60, width = 8 };
            var roads = new[] { main, side };
            Assert.IsTrue(YardScenery.OnOtherRoad(new Vector2(50, 6.6f), main, roads), "Main road's edge at the side road's mouth.");
            Assert.IsFalse(YardScenery.OnOtherRoad(new Vector2(20, 6.6f), main, roads));
            Assert.IsTrue(YardScenery.OnOtherRoad(new Vector2(50, 3f), side, roads), "Side road's end inside the main road.");
            Assert.AreEqual(57f, YardScenery.Surface(main).HalfLength, 1e-4f, "Surface runs half a width past each end.");
        }

        private static YardSnapshot YardFixture()
        {
            var box = new Func<string, string, string, BlockRecord>((id, hull, day) => new BlockRecord
            {
                block = id, hull = hull, forecast = day, weight_t = 100, step = 1, x_min = 0, x_max = 12, y_min = -5, y_max = 5, z_min = 2,
                z_max = 6, cog_x = 6, cog_y = 0, cog_z = 4,
            });
            StockPlanRow Stay(string block, string location, float x, float y, string from, string until) =>
                new StockPlanRow { block = block, location = location, x = x, y = y, heading_deg = 90, stock_from = from, stock_until = until };
            return new YardSnapshot
            {
                format = YardSnapshot.Format,
                hulls = new[] { new HullRow { id = "H1", name = "H1" }, new HullRow { id = "H2", name = "H2" } },
                vessel_details = new[] { new HullInfo { hull = "H1", vessel_type = "LNG", length_m = 100, beam_m = 20 } },
                hull_geometry = new[] { new HullGeometryRow { hull = "H1", depth_m = 10, draft_m = 6, frame_spacing_m = 0.8f } },
                hull_placements = new[]
                {
                    new HullPlacement { hull = "H1", dock = "D", origin_x = 380, origin_y = 170 },
                    new HullPlacement { hull = "H2", dock = "ELSEWHERE" },
                },
                erection_timeline = new[]
                {
                    box("B1", "H1", "2027-01-10"), box("B2", "H1", "2027-01-20"), box("B3", "H1", "2027-01-25"), box("X1", "H2", "2027-01-01"),
                },
                block_stock_plans = new[]
                {
                    Stay("B1", "PE", 200, 170, "2027-01-01", "2027-01-10"),
                    Stay("B3", "STOCK", -100, -60, "2027-01-05", "2027-01-14"), Stay("B3", "PE", 210, 160, "2027-01-15", null),
                },
                location_geo = new[]
                {
                    new Facility { location = "D", name = "1도크", kind = "도크", x = 430, y = 170, size_x_m = 370, size_y_m = 100 },
                    new Facility { location = "PE", name = "1 PE장", kind = "야드", x = 197.5f, y = 170, size_x_m = 95, size_y_m = 100 },
                },
            }.Normalised();
        }

        [Test]
        public void YardModel_PlacesBlocksByPlanDay()
        {
            var model = YardModel.Build(YardFixture(), new DateTime(2027, 1, 5));
            Assert.AreEqual(new[] { "H1" }, model.Hulls.Select(h => h.Id).ToArray(), "H2 sits on a dock outside this yard.");
            Assert.AreEqual(3, model.Blocks.Count);
            Assert.AreEqual(10f, model.Hulls[0].Info.depth_m, "Geometry merged into the hull particulars.");
            Assert.AreEqual(430f, model.Hulls[0].Placement.dock_position.x, "Dock position from the facility.");
            BlockState At(string id, int month, int day) => YardModel.StateAt(model.Blocks[id], new DateTime(2027, month, day));
            Assert.AreEqual(BlockState.InProduction, YardModel.StateAt(model.Blocks["B1"], new DateTime(2026, 12, 31)));
            Assert.AreEqual(BlockState.Stocked, At("B1", 1, 5));
            Assert.AreEqual(BlockState.Erected, At("B1", 1, 10), "On its erection day it is on the hull.");
            Assert.AreEqual(BlockState.Unplaced, At("B2", 1, 5), "No stocking plan: 미배치.");
            Assert.AreEqual(BlockState.Erected, At("B2", 1, 20));
            Assert.AreEqual(BlockState.InProduction, At("B3", 1, 4));
            Assert.AreEqual("STOCK", model.Blocks["B3"].StayAt(new DateTime(2027, 1, 14)).Location);
            Assert.AreEqual("PE", model.Blocks["B3"].StayAt(new DateTime(2027, 1, 24)).Location, "Open-ended stay lasts until erection.");
            Assert.AreEqual("PE", model.Blocks["B3"].LiftSource(new DateTime(2027, 1, 23)).Location);
            var counts = YardModel.Count(model.Hulls[0].Blocks, model.AsOf);
            Assert.AreEqual((2, 0, 0, 1), (counts.Stocked, counts.Erected, counts.InProduction, counts.Unplaced), "B1 and B3 (from Jan 5) stocked, B2 unplaced.");
            var twin = model.ToTwinFile(model.Hulls[0]);
            Assert.AreEqual("D", twin.placement.dock);
            Assert.AreEqual(2, twin.yard.facilities.Length);
        }

        [Test]
        public void YardDataSource_AssemblesErpRoutesLikeTheSnapshot()
        {
            var fixture = YardFixture();
            string Array<T>(T[] rows) => "[" + string.Join(",", rows.Select(r => JsonUtility.ToJson(r))) + "]";
            var bodies = new Dictionary<string, string>
            {
                { "hulls", Array(fixture.hulls) }, { "vessel_details", Array(fixture.vessel_details) },
                { "hull_geometry", Array(fixture.hull_geometry) }, { "hull_placements", Array(fixture.hull_placements) },
                { "erection_timeline", Array(fixture.erection_timeline) }, { "block_stock_plans", Array(fixture.block_stock_plans) },
                { "location_geo", Array(fixture.location_geo) }, { "yard_sites", "[]" },
            };
            CollectionAssert.AreEquivalent(YardDataSource.Routes, bodies.Keys);
            var snapshot = YardDataSource.Assemble(bodies, "test");
            Assert.AreEqual(YardSnapshot.Format, snapshot.format);
            Assert.AreEqual(3, snapshot.block_stock_plans.Length);
            var model = YardModel.Build(snapshot, new DateTime(2027, 1, 5));
            Assert.AreEqual(3, model.Blocks.Count);
            Assert.AreEqual(BlockState.Stocked, YardModel.StateAt(model.Blocks["B1"], model.AsOf));
            Assert.Throws<FormatException>(() => YardSnapshot.Parse("{\"format\":\"other\"}"));
        }

        [Test]
        public void BlockMotion_LiftsFromTheSlotOrDrivesToTheCraneFirst()
        {
            var model = YardModel.Build(YardFixture(), new DateTime(2027, 1, 5));
            var hull = model.Hulls[0];
            var b1 = model.Blocks["B1"];
            var reach = BlockMotion.CraneReach(hull.Dock);
            Assert.IsTrue(reach.Contains(new Vector2(197.5f, 170f)), "The PE yard at the dock head is under the rails.");
            Assert.IsFalse(reach.Contains(new Vector2(-100f, -60f)), "The block stockyard is not.");
            var slot = BlockMotion.InYard(b1.Record, b1.Stays[0]);
            Assert.AreEqual(YardLayout.QuayLevel + BlockMotion.Dunnage + 2f, slot.position.y, 1e-4f, "Resting on its bottom (cog 4, z_min 2).");
            Assert.AreEqual(90f, slot.rotation.eulerAngles.y > 180f ? 360f - slot.rotation.eulerAngles.y : slot.rotation.eulerAngles.y, 1e-3f);
            var landed = BlockMotion.Landed(hull.Placement, b1.Record);
            Assert.AreEqual(new Vector3(386f, 4f, 170f), landed.position);
            Assert.AreEqual(slot.position, BlockMotion.Lift(slot, landed, 0f, reach).Pose.position);
            Assert.AreEqual(landed.position, BlockMotion.Lift(slot, landed, 1f, reach).Pose.position);
            var carried = BlockMotion.Lift(slot, landed, 0.5f, reach);
            Assert.IsTrue(carried.Hooked);
            Assert.Greater(carried.Pose.position.y, landed.position.y + 20f, "Carried high over the dock wall.");

            var far = BlockMotion.InYard(model.Blocks["B3"].Record, model.Blocks["B3"].Stays[0]);   // block stockyard, out of reach
            var driving = BlockMotion.Lift(far, landed, BlockMotion.GroundShare * 0.5f, reach);
            Assert.IsFalse(driving.Hooked, "Driven to the crane on the ground first.");
            Assert.AreEqual(far.position.y, driving.Pose.position.y, 1e-4f);
            var atCrane = BlockMotion.Lift(far, landed, BlockMotion.GroundShare, reach);
            Assert.AreEqual(reach.Pickup, BlockMotion.YardOf(atCrane.Pose.position));
            Assert.IsTrue(atCrane.Hooked);
            var noSlot = BlockMotion.Lift(null, landed, 0.25f, reach);
            Assert.Greater(noSlot.Pose.position.y, landed.position.y, "Unplaced blocks come in from the dock side at hook height.");
        }

        private static float Area(System.Collections.Generic.List<Vector2> p)
        {
            var a = 0f;
            for (var i = 0; i < p.Count; i++)
            {
                a += p[i].x * p[(i + 1) % p.Count].y - p[(i + 1) % p.Count].x * p[i].y;
            }

            return Mathf.Abs(a) / 2f;
        }

        [Test]
        public void Terrain_InterpolatesGridAndMapsMercator()
        {
            var meta = new SatelliteMeta { zoom = 16, tile_x0 = 0, tile_y0 = 0, size_px = 512, grid = 2 };
            var terrain = new TerrainModel(meta, new float[] { 0, 10, 20, 0, 10, 20, 0, 10, 20 });
            Assert.AreEqual(10f, terrain.AtPixel(256, 100), 1e-4f, "Middle column.");
            Assert.AreEqual(15f, terrain.AtPixel(384, 0), 1e-4f, "Halfway between columns 1 and 2.");
            Assert.AreEqual(20f, terrain.AtPixel(9999, 0), 1e-4f, "Clamped at the edge.");
            Assert.Throws<ArgumentException>(() => new TerrainModel(meta, new float[3]));
            var (x, y) = ShipCoordinates.GeoToMercatorPixel(16, 34.8331, 128.5975);
            var (lat, lon) = ShipCoordinates.MercatorPixelToGeo(16, x, y);
            Assert.AreEqual(34.8331, lat, 1e-9);
            Assert.AreEqual(128.5975, lon, 1e-9);
        }

        [Test]
        public void Dashboard_StartsClosedAndPausedWithRailControls()
        {

            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>(
                "Assets/ShipyardTwin/Authoring/Dashboard.uxml");
            Assert.IsNotNull(asset);
            var root = asset.CloneTree();
            var rail = root.Q("rail");
            foreach (var name in new[] { "nav-erection", "nav-depth", "nav-chat" })
            {
                Assert.IsNotNull(rail.Q<UnityEngine.UIElements.Button>(name), name + " sits in the rail.");
            }

            foreach (var name in new[] { "exit", "erection-panel", "chat-panel", "depth-legend", "exit-confirm", "play", "speed", "restart", "events" })
            {
                Assert.IsNotNull(root.Q(name), name);
            }

            Assert.IsNull(rail.Q("exit"), "Exit is top right, not in the rail.");
            Assert.IsFalse(root.Q("erection-panel").ClassListContains("tw-drawer--open"), "Erection panel starts closed.");
            Assert.IsFalse(root.Q("chat-panel").ClassListContains("tw-drawer--open"), "Chat panel starts closed.");
        }
    }
}
