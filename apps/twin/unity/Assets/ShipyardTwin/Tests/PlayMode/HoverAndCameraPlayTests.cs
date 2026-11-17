using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ShipyardTwin.Tests
{
    /// <summary>
    /// Play Mode checks in the generated scene: what the hover tooltip names under a screen point (the same
    /// <see cref="HoverTooltipView.Pick"/> the tooltip uses every frame) and how mouse buttons move the camera.
    /// </summary>
    public sealed class HoverAndCameraPlayTests
    {
        private Camera _camera;
        private OrbitCameraRig _rig;
        private ShipView _ship;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            SceneManager.LoadScene("ShipyardTwin");
            ShipyardTwinBootstrap.AsOfOverride = "2026-11-17";
            var deadline = Time.realtimeSinceStartup + 60f;
            for (var i = 0; Time.realtimeSinceStartup < deadline && (_ship == null || !_ship.Ready || _rig == null); i++)
            {
                yield return null;
                _ship = Object.FindFirstObjectByType<ShipView>();
                _rig = Object.FindFirstObjectByType<OrbitCameraRig>();
            }

            _camera = Camera.main;
            Assert.IsTrue(_ship != null && _ship.Ready, "Scene constructed and yard blocks placed.");
            Assert.IsNotNull(_camera);
        }

        private HoverTarget PickAt(Vector3 world)
        {
            var screen = _camera.WorldToScreenPoint(world);
            Assert.Greater(screen.z, 0f, "In front of the camera.");
            return HoverTooltipView.Pick(_camera, screen);
        }

        private IEnumerator Look(Vector3 pivot, float yaw, float pitch, float distance)
        {
            _camera.transform.rotation = Quaternion.Euler(pitch, yaw, 0);
            _camera.transform.position = pivot - _camera.transform.rotation * Vector3.forward * distance;
            _rig.enabled = false;   // hold the pose
            yield return null;
        }

        [UnityTest]
        public IEnumerator Hover_NamesBlocksFacilitiesAndTheShip()
        {
            var twin = _ship.Model.ToTwinFile(_ship.Model.Hull("SN2040"));

            var dock = twin.yard.facilities.First(f => f.location == twin.placement.dock);
            var dockTop = ShipCoordinates.YardToUnity(dock.x, dock.y, YardLayout.QuayLevel);

            // Day 0: no block erected. From straight above the dock the keel blocks name the ship, and no point
            // over a (hidden) block's landed place names a block.
            _ship.Select("SN2040");
            _ship.Playback.Seek(0f);
            yield return Look(dockTop, 0f, 89f, 520f);
            var keel = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).First(t => t.name == "KeelBlocks SN2040");
            var keelPicks = keel.GetComponentsInChildren<Renderer>().Where(r => r.name == "Keel")
                .Select(r => PickAt(r.bounds.center + Vector3.up * r.bounds.extents.y)).Where(t => t != null).ToList();
            Assert.IsTrue(keelPicks.Any(t => t.Kind == "선박"), "Keel blocks name the ship.");
            var ship = keelPicks.First(t => t.Kind == "선박");
            Assert.AreEqual(twin.hull.vessel_name, ship.Title);
            StringAssert.Contains(twin.hull.hull, ship.Detail);
            var blockTargets = _ship.GetComponentsInChildren<HoverTarget>().Where(t => t.Kind == "블록" && t.Title.StartsWith("SN2040-")).ToList();
            Assert.Greater(blockTargets.Count, 100);
            var hidden = blockTargets.Where(b => !b.GetComponent<Collider>().enabled).ToList();
            Assert.Greater(hidden.Count, 0, "Some blocks are still in production on day 0.");
            Assert.IsFalse(hidden.Select(b => PickAt(_ship.transform.TransformPoint(b.transform.localPosition))).Any(t => hidden.Contains(t)),
                "Hidden blocks are never picked.");

            // All erected: the top face of each block, seen from above, names that block unless the goliath is over it.
            _ship.Playback.Seek(_ship.Playback.Schedule.Duration + 10f);
            yield return null;
            yield return Look(dockTop, 0f, 89f, 520f);
            var own = 0;
            foreach (var target in blockTargets)
            {
                var r = target.GetComponent<Renderer>();
                var hit = PickAt(new Vector3(r.bounds.center.x, r.bounds.max.y, r.bounds.center.z));
                if (hit == target) own++;
                if (hit != null) Assert.AreNotEqual("시설", hit.Kind, "No facility picked through the ship.");
            }

            Assert.Greater(own, blockTargets.Count / 4, "Blocks name themselves from above.");
            var block = blockTargets.First(t => PickAt(new Vector3(t.GetComponent<Renderer>().bounds.center.x,
                t.GetComponent<Renderer>().bounds.max.y, t.GetComponent<Renderer>().bounds.center.z)) == t);
            StringAssert.StartsWith(twin.hull.hull + "-", block.Title, "Block ID.");
            StringAssert.Contains("호선 " + twin.hull.hull, block.Detail);

            // The goliath crane occludes: its girder top is never named, nor anything under it.
            var gantry = Object.FindObjectsByType<GantryCraneView>(FindObjectsSortMode.None)
                .SelectMany(g => g.GetComponentsInChildren<Renderer>()).OrderByDescending(r => r.bounds.max.y).First();
            Assert.IsNull(PickAt(new Vector3(gantry.bounds.center.x, gantry.bounds.max.y, gantry.bounds.center.z)), "Crane is an occluder.");

            // A shop roof: the ERP facility name.
            var shop = twin.yard.facilities.First(f => f.location == "GY-ASSY1");
            var roof = ShipCoordinates.YardToUnity(shop.x, shop.y, YardLayout.QuayLevel + 20f);
            yield return Look(roof, 0f, 89f, 200f);
            var hall = PickAt(ShipCoordinates.YardToUnity(shop.x, shop.y, YardLayout.QuayLevel));
            Assert.IsNotNull(hall);
            Assert.AreEqual("시설", hall.Kind);
            Assert.AreEqual(shop.name, hall.Title);

            // Open sea and the paved road: nothing.
            Assert.IsNull(PickAt(ShipCoordinates.YardToUnity(shop.x - 1500f, shop.y + 3000f, YardLayout.SeaLevel)), "Far sea.");
            yield return Look(ShipCoordinates.YardToUnity(dock.x + dock.size_x_m / 2f + 300f, dock.y, YardLayout.SeaLevel), 0f, 89f, 200f);
            Assert.IsNull(PickAt(ShipCoordinates.YardToUnity(dock.x + dock.size_x_m / 2f + 300f, dock.y, YardLayout.SeaLevel)), "Sea off the gate.");

            // A dock (ERP facility) wall top, sampled along its length (the goliath may stand over part of it).
            yield return Look(dockTop, 0f, 89f, 520f);
            var wall = Enumerable.Range(0, 9).Select(i => dock.x - dock.size_x_m / 2f + 20f + i * (dock.size_x_m - 40f) / 8f)
                .Select(x => PickAt(ShipCoordinates.YardToUnity(x, dock.y + dock.size_y_m / 2f - 3f, YardLayout.QuayLevel)))
                .Where(t => t != null).ToList();
            Assert.IsTrue(wall.Any(t => t.Title == dock.name && t.Kind == "시설"), "Dock wall names the dock.");
        }

        [UnityTest]
        public IEnumerator Hover_HiddenOverUiWhileDraggingAndOffScreen()
        {
            var size = new Vector2(1600, 900);
            Assert.IsTrue(HoverTooltipView.Allowed(false, false, new Vector2(800, 450), size));
            Assert.IsFalse(HoverTooltipView.Allowed(true, false, new Vector2(800, 450), size), "Over the dashboard.");
            Assert.IsFalse(HoverTooltipView.Allowed(false, true, new Vector2(800, 450), size), "Camera drag.");
            Assert.IsFalse(HoverTooltipView.Allowed(false, false, new Vector2(-5, 450), size), "Off screen.");
            var tooltip = Object.FindFirstObjectByType<HoverTooltipView>();
            Assert.IsNotNull(tooltip, "Tooltip view in the scene.");
            yield return null;
        }

        [UnityTest]
        public IEnumerator Camera_LeftPansRightOrbitsMiddlePansWheelZooms()
        {
            _rig.SetView("overview", snap: true);
            yield return null;
            Vector3 pivot = _rig.PivotGoal;
            float yaw = _rig.YawGoal, pitch = _rig.PitchGoal, distance = _rig.DistanceGoal;

            // Left drag sideways: the pivot slides along the camera's right axis (the screen plane), no orbit.
            _rig.ApplyMouse(true, false, false, new Vector2(40f, 0f), new Vector2(3f, 2f), 0f);
            var moved = _rig.PivotGoal - pivot;
            Assert.Greater(moved.magnitude, 1f, "Left drag pans.");
            Assert.AreEqual(yaw, _rig.YawGoal, 1e-4f, "Left drag does not orbit.");
            Assert.AreEqual(pitch, _rig.PitchGoal, 1e-4f);
            var right = Quaternion.Euler(pitch, yaw, 0) * Vector3.right;
            Assert.AreEqual(-1f, Vector3.Dot(moved.normalized, right), 1e-3f, "Against the drag along the screen's x axis.");
            // Left drag up/down moves along the screen's y axis (kept above the ground).
            pivot = _rig.PivotGoal;
            _rig.ApplyMouse(true, false, false, new Vector2(0f, -3f), Vector2.zero, 0f);
            Assert.Greater(Vector3.Distance(_rig.PivotGoal, pivot), 0.01f, "Vertical left drag pans.");
            Assert.GreaterOrEqual(_rig.PivotGoal.y, 0f);

            pivot = _rig.PivotGoal;
            _rig.ApplyMouse(false, true, false, new Vector2(40f, 25f), new Vector2(3f, 2f), 0f);
            Assert.AreNotEqual(yaw, _rig.YawGoal, "Right drag orbits.");
            Assert.AreEqual(0f, Vector3.Distance(_rig.PivotGoal, pivot), 1e-4f, "Right drag does not pan.");

            yaw = _rig.YawGoal;
            _rig.ApplyMouse(false, false, true, Vector2.zero, new Vector2(3f, 2f), 0f);
            Assert.Greater(Vector3.Distance(_rig.PivotGoal, pivot), 1f, "Middle drag pans.");
            Assert.AreEqual(pivot.y, _rig.PivotGoal.y, 1e-4f, "Middle drag stays on the ground plane.");
            Assert.AreEqual(yaw, _rig.YawGoal, 1e-4f);

            _rig.ApplyMouse(false, false, false, Vector2.zero, Vector2.zero, 1f);
            Assert.Less(_rig.DistanceGoal, distance, "Wheel up zooms in.");

            // A press over the UI blocks the whole drag until release; a press in the scene does not.
            Assert.IsTrue(OrbitCameraRig.DragBlocked(false, pressed: true, held: true, overUi: true));
            Assert.IsTrue(OrbitCameraRig.DragBlocked(true, pressed: false, held: true, overUi: false), "Still blocked off the UI.");
            Assert.IsFalse(OrbitCameraRig.DragBlocked(true, pressed: false, held: false, overUi: false), "Released.");
            Assert.IsFalse(OrbitCameraRig.DragBlocked(false, pressed: true, held: true, overUi: false));
        }
    }
}
