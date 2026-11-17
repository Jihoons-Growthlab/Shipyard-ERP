using System;
using System.Linq;
using GrowthLab.Core;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Orbit camera around the dock. Mouse: left-drag pans along the screen plane (the point under the cursor follows
    /// it), right-drag orbits around the pivot, middle-drag pans over the ground, wheel zooms. A drag that starts on
    /// the dashboard UI does nothing, and the wheel is ignored over it.
    /// Keys: 1 overview, 2 bow, 3 stern, 4 crane close-up, 5 top-down, 6 whole yard, F follow the lifted block.
    /// Command line <c>-shipyard-view &lt;overview|bow|stern|crane|top|yard|follow&gt;</c> picks the start view;
    /// <c>-shipyard-camera x,y,yaw,pitch,distance</c> places it anywhere (yard metres, for checks and screenshots).
    /// Uses the classic Input Manager (Project Settings: Active Input Handling = Input Manager or Both).
    /// </summary>
    public sealed class OrbitCameraRig : MonoBehaviour, ICoreConsumer
    {
        public const string ViewArgument = "-shipyard-view";

        /// <summary>Command line <c>-shipyard-camera x,y,yaw,pitch,distance</c>: orbit pivot at yard (x, y) on the quay.</summary>
        public const string CameraArgument = "-shipyard-camera";

        [SerializeField] private float _orbitSpeed = 0.25f;
        [SerializeField] private float _zoomSpeed = 0.12f;
        [SerializeField] private float _damping = 6f;

        private Camera _camera;
        private ShipView _ship;
        private Vector3 _dockCentre;
        private float _hullLength;
        private Vector3 _yardCentre;
        private float _yardSize;
        private Vector3 _pivot, _pivotGoal;
        private float _yaw, _pitch, _distance, _yawGoal, _pitchGoal, _distanceGoal;
        private bool _follow;
        private bool _dragFromUi;
        private Vector2 _lastMouse;

        public Vector3 PivotGoal => _pivotGoal;

        /// <summary>The camera has reached its goal and is not following a lift (nothing to animate).</summary>
        public bool Settled => !(_follow && _ship != null && _ship.CurrentLift != null) && (_pivot - _pivotGoal).sqrMagnitude < 0.0004f &&
                               Mathf.Abs(Mathf.DeltaAngle(_yaw, _yawGoal)) < 0.02f && Mathf.Abs(_pitch - _pitchGoal) < 0.02f &&
                               Mathf.Abs(_distance - _distanceGoal) < 0.02f;
        public float YawGoal => _yawGoal;
        public float PitchGoal => _pitchGoal;
        public float DistanceGoal => _distanceGoal;

        public string View { get; private set; } = "overview";

        public void Construct(CoreContext context)
        {
            if (_camera != null || !context.Services.TryGet<TwinFile>(out var twin))
            {
                return;
            }

            _camera = Camera.main;
            _ship = FindFirstObjectByType<ShipView>();
            _dockCentre = YardLayout.DockCentre(twin) + Vector3.up * 14f;
            _hullLength = twin.hull.length_m;
            var plan = twin.yard?.facilities?.Where(f => f.HasFootprint).ToList();
            if (plan != null && plan.Count > 0)
            {
                float x0 = plan.Min(f => f.x - f.size_x_m / 2f), x1 = plan.Max(f => f.x + f.size_x_m / 2f);
                float y0 = plan.Min(f => f.y - f.size_y_m / 2f), y1 = plan.Max(f => f.y + f.size_y_m / 2f);
                _yardCentre = ShipCoordinates.YardToUnity((x0 + x1) / 2f, (y0 + y1) / 2f, 12f);
                _yardSize = Mathf.Max(x1 - x0, y1 - y0);
            }
            else
            {
                _yardCentre = _dockCentre;
                _yardSize = _hullLength * 1.5f;
            }
            SetView(ArgumentValue(ViewArgument) ?? "overview", snap: true);
            var custom = ParseCamera(ArgumentValue(CameraArgument));
            if (custom != null)
            {
                _follow = false;
                (_pivot, _yaw, _pitch, _distance) = (_pivotGoal, _yawGoal, _pitchGoal, _distanceGoal) = custom.Value;
                Place();
            }
        }

        /// <summary>"x,y,yaw,pitch,distance" (yard metres, degrees) → orbit preset; null when absent or malformed.</summary>
        public static (Vector3 Pivot, float Yaw, float Pitch, float Distance)? ParseCamera(string value)
        {
            var parts = value?.Split(',');
            if (parts == null || parts.Length != 5)
            {
                return null;
            }

            var v = new float[5];
            for (var i = 0; i < 5; i++)
            {
                if (!float.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v[i]))
                {
                    return null;
                }
            }

            return (ShipCoordinates.YardToUnity(v[0], v[1], YardLayout.QuayLevel), v[2], Mathf.Clamp(v[3], 3f, 89f), Mathf.Max(5f, v[4]));
        }

        /// <summary>Applies a named preset (unknown names fall back to the overview).</summary>
        public void SetView(string name, bool snap = false)
        {
            _follow = name == "follow" || name == "crane";
            var half = _hullLength / 2f;
            (Vector3 pivot, float yaw, float pitch, float distance) preset = name switch
            {
                // yaw 0 looks north (+z, ship port side away from the camera); 90 looks forward along the hull.
                "bow" => (_dockCentre + new Vector3(half - 25f, -2f, 0), 305f, 12f, 125f),
                "stern" => (_dockCentre + new Vector3(-half + 28f, -6f, 0), 55f, 9f, 105f),
                "crane" => (_dockCentre, 22f, 16f, 175f),
                "follow" => (_dockCentre, 35f, 24f, 210f),
                "top" => (_dockCentre, 0f, 72f, 430f),
                "yard" => (_yardCentre, 40f, 38f, _yardSize * 1.25f),
                "pier" => (_dockCentre + new Vector3(505f, -4f, -320f), 120f, 22f, 520f),
                "coast" => (_dockCentre + new Vector3(66f, -4f, -303f), 331f, 15f, 170f),
                _ => (_dockCentre + new Vector3(-10f, 0, 0), 50f, 22f, 400f),
            };
            View = name;
            (_pivotGoal, _yawGoal, _pitchGoal, _distanceGoal) = preset;
            if (snap)
            {
                (_pivot, _yaw, _pitch, _distance) = preset;
                if (_follow)
                {
                    FollowTarget(1f);
                    _pivot = _pivotGoal;
                }

                Place();
            }
        }

        private void Update()
        {
            if (_camera == null)
            {
                return;
            }

            HandleInput();
            if (_follow)
            {
                FollowTarget(Time.deltaTime);
            }

            var t = 1f - Mathf.Exp(-_damping * Time.deltaTime);
            _pivot = Vector3.Lerp(_pivot, _pivotGoal, t);
            _yaw = Mathf.LerpAngle(_yaw, _yawGoal, t);
            _pitch = Mathf.Lerp(_pitch, _pitchGoal, t);
            _distance = Mathf.Lerp(_distance, _distanceGoal, t);
            Place();
        }

        private void FollowTarget(float dt)
        {
            if (_ship != null && _ship.CurrentLift != null)
            {
                var target = _ship.CurrentLiftBounds.center;
                _pivotGoal = Vector3.Lerp(_pivotGoal, new Vector3(target.x, Mathf.Max(target.y - 10f, 10f), _dockCentre.z), Mathf.Clamp01(dt * 3f));
            }
        }

        private void HandleInput()
        {
            for (var key = 0; key < 6; key++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + key))
                {
                    SetView(new[] { "overview", "bow", "stern", "crane", "top", "yard" }[key]);
                }
            }

            if (Input.GetKeyDown(KeyCode.F))
            {
                _follow = !_follow;
            }

            // A drag that starts on the dashboard (rail, drawers, buttons) never moves the camera.
            var pressed = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2);
            var held = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
            _dragFromUi = DragBlocked(_dragFromUi, pressed, held, DashboardShellView.PointerOverUi);
            var mouse = (Vector2)Input.mousePosition;
            var pixels = pressed ? Vector2.zero : mouse - _lastMouse;
            _lastMouse = mouse;
            if (_dragFromUi)
            {
                return;
            }

            ApplyMouse(Input.GetMouseButton(0), Input.GetMouseButton(1), Input.GetMouseButton(2), pixels,
                new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")), DashboardShellView.PointerOverUi ? 0f : Input.mouseScrollDelta.y);
        }

        /// <summary>A drag is blocked from its press (over the UI) until every button is released.</summary>
        public static bool DragBlocked(bool wasBlocked, bool pressed, bool held, bool overUi) => pressed ? overUi : held && wasBlocked;

        /// <summary>
        /// One frame of mouse input. Left: pan along the screen plane by the cursor's pixel motion. Right: orbit
        /// (axis motion). Middle: pan over the ground (axis motion). Wheel: zoom.
        /// </summary>
        public void ApplyMouse(bool left, bool right, bool middle, Vector2 pixels, Vector2 axis, float wheel)
        {
            if (left && pixels.sqrMagnitude > 0f)
            {
                _follow = false;
                var fov = _camera != null ? _camera.fieldOfView : 60f;
                var metresPerPixel = 2f * _distance * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height);
                var rotation = Quaternion.Euler(_pitch, _yaw, 0);
                _pivotGoal -= (rotation * Vector3.right * pixels.x + rotation * Vector3.up * pixels.y) * metresPerPixel;
                _pivotGoal.y = Mathf.Clamp(_pivotGoal.y, 0f, 400f);
            }

            if (right)
            {
                _yawGoal += axis.x * _orbitSpeed * 20f;
                _pitchGoal = Mathf.Clamp(_pitchGoal - axis.y * _orbitSpeed * 20f, 3f, 85f);
            }

            if (middle)
            {
                _follow = false;
                var rotation = Quaternion.Euler(0, _yaw, 0);
                var scale = _distance * 0.004f * 20f;
                _pivotGoal -= rotation * new Vector3(axis.x, 0, axis.y) * scale;
            }

            if (Math.Abs(wheel) > 0.01f)
            {
                _distanceGoal = Mathf.Clamp(_distanceGoal * (1f - wheel * _zoomSpeed), 25f, 1500f);
            }
        }

        private void Place()
        {
            var rotation = Quaternion.Euler(_pitch, _yaw, 0);
            _camera.transform.position = _pivot - rotation * Vector3.forward * _distance;
            _camera.transform.rotation = rotation;
        }

        private static string ArgumentValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
