using System.Collections.Generic;
using System.Linq;
using GrowthLab.Core;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Shipyard goliath gantry crane spanning the building dock, built from primitives: a rigid A-frame leg on the
    /// north quay and a slender flexible leg on the south quay (the usual rigid/flexible pair that tolerates long
    /// rails), twin box girders with walkways, an upper trolley with two hooks running on the girders and a lower
    /// trolley with one hook under them, wheel bogies on quay rails, operator cab and aviation lights.
    /// The crane travels to the block being lifted; the upper trolley's two hooks carry it on four slings.
    /// Every other dock gets its own goliath (built by this one), parked over its PE yard at the dock head; the
    /// rails run from the PE yard to the gate. Illustrative only: it follows the playback, it does not plan lifts.
    /// </summary>
    public sealed class GantryCraneView : MonoBehaviour, ICoreConsumer
    {
        [Tooltip("Girder underside height above the quay (m).")]
        [SerializeField] private float _height = 92f;

        [Tooltip("Rail clearance beyond each dock wall (m).")]
        [SerializeField] private float _railOffset = 8f;

        [Tooltip("Follow rate (1/s at speed ×1).")]
        [SerializeField] private float _followRate = 3f;

        private ShipView _ship;
        private ShipPalette _palette;
        private Transform _gantry, _upperTrolley, _lowerTrolley;
        private readonly List<Transform> _hooks = new List<Transform>();
        private readonly List<Transform> _falls = new List<Transform>();
        private readonly List<Transform> _slings = new List<Transform>();
        private float _span, _centreZ, _girderY;
        private bool _placed;
        private Facility _dock;
        private bool _parked;
        private string _dockId;
        private float _railStart, _railEnd, _parkX;
        private bool _settled;

        /// <summary>Rail extension inland of the dock (over the PE yard at the dock head), m.</summary>
        private const float HeadRail = 100f;

        public void Construct(CoreContext context)
        {
            if (_gantry != null || !context.Services.TryGet<TwinFile>(out var twin) || !context.Services.TryGet(out _palette))
            {
                return;
            }

            _ship = FindFirstObjectByType<ShipView>();
            var centre = _dock != null ? ShipCoordinates.YardToUnity(_dock.x, _dock.y, 0f) : YardLayout.DockCentre(twin);
            var pitHalfWidth = _dock != null ? _dock.size_y_m / 2f - YardLayout.WallThickness : YardLayout.PitHalfWidth(twin);
            var pit = _dock != null ? _dock.size_x_m / 2f - YardLayout.WallThickness : YardLayout.PitHalfLength(twin);
            _centreZ = centre.z;
            _span = 2f * (pitHalfWidth + YardLayout.WallThickness + _railOffset);
            _girderY = _height;
            transform.position = new Vector3(centre.x, YardLayout.QuayLevel, centre.z);

            var halfSpan = _span / 2f;
            // Rails run from over the PE yard at the dock head to just short of the gate (not over the sea).
            float railStart = -pit - HeadRail, railEnd = pit - 10f;
            _railStart = railStart;
            _railEnd = railEnd;
            _parkX = -pit - HeadRail / 2f;
            _dockId = _dock != null ? _dock.location : twin.placement.dock;
            foreach (var side in new[] { -1f, 1f })
            {
                Box("Rail", transform, new Vector3((railStart + railEnd) / 2f, 0.25f, side * halfSpan), new Vector3(railEnd - railStart, 0.5f, 1.2f), "rail");
            }

            _gantry = new GameObject("Gantry").transform;
            _gantry.SetParent(transform, false);
            // Detailed Blender models (pipeline/build_hq_assets.py) when present, primitives otherwise.
            if (!PropLibrary.TryCreate("goliath_frame", _palette, _gantry, out _))
            {
                BuildLegs(halfSpan);
                BuildGirders(halfSpan);
            }

            BuildTrolleys(halfSpan);
            HoverTarget.Occluder(gameObject);   // a block or the ship behind the crane is not named through it
            _gantry.localPosition = new Vector3(_parkX, 0, 0);   // waiting over the PE yard until its dock's hull is selected
            if (_parked)
            {
                return;
            }

            // One goliath per other dock, parked over its PE yard.
            foreach (var dock in YardLayout.Docks(twin).Where(d => d.location != twin.placement.dock))
            {
                var other = new GameObject("GoliathCrane " + dock.name).AddComponent<GantryCraneView>();
                other._dock = dock;
                other._parked = true;
                other._height = _height;
                other._railOffset = _railOffset;
                other.Construct(context);
            }
        }

        private void BuildLegs(float halfSpan)
        {
            var top = _girderY - 3.5f;
            // Rigid leg (north, +z): A-frame of two box columns, wide at the bogies, with ties.
            var rigid = halfSpan;
            foreach (var s in new[] { -1f, 1f })
            {
                Beam("RigidLeg", new Vector3(s * 13f, 3.5f, rigid), new Vector3(s * 4.5f, top, rigid), 3.6f, "crane_white");
            }

            Box("RigidTie", _gantry, new Vector3(0, _girderY * 0.42f, rigid), new Vector3(18f, 2.2f, 2.6f), "crane_white");
            Box("RigidTie", _gantry, new Vector3(0, _girderY * 0.75f, rigid), new Vector3(11f, 2.0f, 2.6f), "crane_white");
            Box("RigidHead", _gantry, new Vector3(0, top, rigid), new Vector3(14f, 4f, 5f), "crane_white");
            // Flexible leg (south, -z): two slender columns.
            var flexible = -halfSpan;
            foreach (var s in new[] { -1f, 1f })
            {
                Beam("FlexLeg", new Vector3(s * 5f, 3.5f, flexible), new Vector3(s * 4.5f, top, flexible), 2.4f, "crane_white");
            }

            Box("FlexHead", _gantry, new Vector3(0, top, flexible), new Vector3(12f, 3.2f, 4f), "crane_white");
            Box("FlexTie", _gantry, new Vector3(0, _girderY * 0.55f, flexible), new Vector3(10f, 1.4f, 1.8f), "crane_white");
            // Sill beams and wheel bogies on the rails.
            foreach (var (z, half) in new[] { (rigid, 17f), (flexible, 9f) })
            {
                Box("Sill", _gantry, new Vector3(0, 3f, z), new Vector3(half * 2f, 2.4f, 3f), "crane_red");
                foreach (var s in new[] { -1f, 1f })
                {
                    Box("Bogie", _gantry, new Vector3(s * (half - 2.5f), 1.1f, z), new Vector3(5f, 1.8f, 2.6f), "crane_dark");
                    foreach (var w in new[] { -1.4f, 1.4f })
                    {
                        Cylinder("Wheel", _gantry, new Vector3(s * (half - 2.5f) + w, 0.55f, z), Vector3.forward, 0.55f, 3f, "crane_dark");
                    }
                }
            }
        }

        private void BuildGirders(float halfSpan)
        {
            var length = _span + 14f;
            foreach (var s in new[] { -1f, 1f })
            {
                var x = s * 3.8f;
                Box("Girder", _gantry, new Vector3(x, _girderY + 3.5f, 0), new Vector3(3.2f, 7f, length), "crane_white");
                Box("GirderStripe", _gantry, new Vector3(x + s * 1.62f, _girderY + 5.4f, 0), new Vector3(0.06f, 1.5f, length), "crane_red");
                Box("TrolleyRail", _gantry, new Vector3(x, _girderY + 7.15f, 0), new Vector3(0.6f, 0.3f, length - 4f), "rail");
                Box("Walkway", _gantry, new Vector3(x + s * 2.6f, _girderY + 0.4f, 0), new Vector3(2f, 0.15f, length - 2f), "crane_dark");
                Box("Handrail", _gantry, new Vector3(x + s * 3.55f, _girderY + 1.5f, 0), new Vector3(0.08f, 0.08f, length - 2f), "crane_yellow");
                foreach (var z in new[] { -length / 2f + 1f, length / 2f - 1f })
                {
                    Box("AviationLight", _gantry, new Vector3(x, _girderY + 7.6f, z), new Vector3(0.7f, 0.7f, 0.7f), "crane_red");
                }
            }

            Box("EndBeam", _gantry, new Vector3(0, _girderY + 3.5f, halfSpan), new Vector3(11f, 6f, 3f), "crane_white");
            Box("EndBeam", _gantry, new Vector3(0, _girderY + 3.5f, -halfSpan), new Vector3(11f, 6f, 3f), "crane_white");
            Box("Cab", _gantry, new Vector3(-5.5f, _girderY - 2.2f, halfSpan - 14f), new Vector3(3.2f, 3f, 4f), "glass");
            Box("CabFrame", _gantry, new Vector3(-5.5f, _girderY - 0.55f, halfSpan - 14f), new Vector3(3.4f, 0.4f, 4.2f), "crane_white");
        }

        private void BuildTrolleys(float halfSpan)
        {
            // Upper trolley on the girders: frame, machinery house, two hoists over the gap between girders.
            _upperTrolley = new GameObject("UpperTrolley").transform;
            _upperTrolley.SetParent(_gantry, false);
            _upperTrolley.localPosition = new Vector3(0, _girderY + 7.3f, 0);
            var detailedTrolley = PropLibrary.TryCreate("goliath_upper_trolley", _palette, _upperTrolley, out _);
            if (!detailedTrolley)
            {
                Box("Frame", _upperTrolley, new Vector3(0, 1.4f, 0), new Vector3(12f, 2.8f, 11f), "crane_red");
                Box("House", _upperTrolley, new Vector3(0, 4.8f, 1.2f), new Vector3(10f, 4f, 7.5f), "crane_white");
                Box("HouseRoof", _upperTrolley, new Vector3(0, 6.95f, 1.2f), new Vector3(10.2f, 0.3f, 7.7f), "crane_red");
            }

            foreach (var hx in new[] { -2.6f, 2.6f })
            {
                if (!detailedTrolley)
                {
                    Cylinder("Drum", _upperTrolley, new Vector3(hx, 3.4f, -3.5f), Vector3.right, 0.9f, 2.4f, "crane_dark");
                }

                _hooks.Add(Hook("Hook", hx));
                for (var i = 0; i < 4; i++)
                {
                    _falls.Add(Box("Fall", _gantry, Vector3.zero, Vector3.one, "wire"));
                }
            }

            for (var i = 0; i < 4; i++)
            {
                var sling = Box("Sling", _gantry, Vector3.zero, Vector3.one, "wire");
                sling.gameObject.SetActive(false);
                _slings.Add(sling);
            }

            // Lower trolley under the girders, parked towards the rigid leg with its hook raised.
            _lowerTrolley = new GameObject("LowerTrolley").transform;
            _lowerTrolley.SetParent(_gantry, false);
            _lowerTrolley.localPosition = new Vector3(0, _girderY - 0.2f, halfSpan - 26f);
            if (!PropLibrary.TryCreate("goliath_lower_trolley", _palette, _lowerTrolley, out _))
            {
                Box("Body", _lowerTrolley, new Vector3(0, -2f, 0), new Vector3(9.5f, 3.6f, 6f), "crane_red");
                Box("Wire", _lowerTrolley, new Vector3(0, -7f, 0), new Vector3(0.25f, 6.5f, 0.25f), "wire");
                Box("Block", _lowerTrolley, new Vector3(0, -11f, 0), new Vector3(2f, 2.6f, 1.3f), "crane_yellow");
            }
        }

        private Transform Hook(string name, float x)
        {
            var hook = new GameObject(name).transform;
            hook.SetParent(_gantry, false);
            if (!PropLibrary.TryCreate("goliath_hook", _palette, hook, out _))
            {
                Box("Block", hook, new Vector3(0, 0.2f, 0), new Vector3(2.4f, 3f, 1.4f), "crane_yellow");
                Box("Stripe", hook, new Vector3(0, 0.2f, 0), new Vector3(2.45f, 0.5f, 1.45f), "crane_dark");
                Box("Shank", hook, new Vector3(0, -1.9f, 0), new Vector3(0.5f, 1.4f, 0.5f), "crane_dark");
                Box("Hook", hook, new Vector3(0.35f, -2.7f, 0), new Vector3(1.2f, 0.4f, 0.5f), "crane_dark");
            }
            hook.localPosition = new Vector3(x, _girderY - 20f, 0);
            return hook;
        }

        private void LateUpdate()
        {
            if (_gantry == null || _ship == null)
            {
                return;
            }

            var multiplier = _ship.Playback?.SpeedMultiplier ?? 1f;
            var blend = _placed ? 1f - Mathf.Exp(-_followRate * multiplier * Time.deltaTime) : 1f;
            var active = _ship.ActiveDock == _dockId;
            var carrying = active && _ship.CurrentLift != null;
            var lifting = carrying && _ship.CurrentLiftHooked;
            var bounds = _ship.CurrentLiftBounds;
            if (!carrying && _settled)
            {
                return;   // parked or idle and already in place: nothing to move this frame
            }

            // Over the lift (or at the rail head while a transporter brings the block), else parked over the PE yard.
            var targetX = Mathf.Clamp(carrying ? bounds.center.x - transform.position.x : _parkX, _railStart + 12f, _railEnd);
            var targetZ = lifting ? Mathf.Clamp(bounds.center.z - _centreZ, -_span / 2f + 9f, _span / 2f - 9f) : 0f;
            var gantry = _gantry.localPosition;
            _gantry.localPosition = new Vector3(Mathf.Lerp(gantry.x, targetX, blend), 0, 0);
            var trolley = _upperTrolley.localPosition;
            _upperTrolley.localPosition = new Vector3(0, trolley.y, Mathf.Lerp(trolley.z, targetZ, blend));
            _placed = true;
            var settled = !carrying && Mathf.Abs(_gantry.localPosition.x - targetX) < 0.05f && Mathf.Abs(_upperTrolley.localPosition.z - targetZ) < 0.05f;

            // Hooks: just above the block's sling points while lifting, otherwise parked high.
            var trolleyZ = _upperTrolley.localPosition.z;
            var slingTop = lifting ? bounds.max.y - transform.position.y + 9f : _girderY - 22f;
            for (var h = 0; h < _hooks.Count; h++)
            {
                var hook = _hooks[h];
                var x = h == 0 ? -2.6f : 2.6f;
                var target = new Vector3(x, slingTop + 2.9f, trolleyZ);
                hook.localPosition = Vector3.Lerp(hook.localPosition, target, _placed ? blend : 1f);
                settled &= (hook.localPosition - target).sqrMagnitude < 0.0025f;
                var top = hook.localPosition + Vector3.up * 1.7f;
                var drum = new Vector3(x, _girderY + 7.3f, trolleyZ);
                for (var i = 0; i < 4; i++)
                {
                    var offset = new Vector3(i < 2 ? -0.5f : 0.5f, 0, i % 2 == 0 ? -0.35f : 0.35f);
                    Beam(_falls[h * 4 + i], drum + offset, top + offset, 0.14f);
                }
            }

            for (var i = 0; i < _slings.Count; i++)
            {
                _slings[i].gameObject.SetActive(lifting);
                if (!lifting)
                {
                    continue;
                }

                var hook = _hooks[i < 2 ? 0 : 1].localPosition + new Vector3(0.35f, -2.9f, 0);
                var cornerX = (i < 2 ? bounds.min.x + bounds.size.x * 0.2f : bounds.max.x - bounds.size.x * 0.2f) - transform.position.x - _gantry.localPosition.x;
                var cornerZ = (i % 2 == 0 ? bounds.min.z + bounds.size.z * 0.2f : bounds.max.z - bounds.size.z * 0.2f) - _centreZ;
                var corner = new Vector3(cornerX, bounds.max.y - transform.position.y, cornerZ);
                Beam(_slings[i], hook, corner, 0.12f);
            }

            _settled = settled;
        }

        private void Beam(string name, Vector3 from, Vector3 to, float thickness, string paint) =>
            Beam(Box(name, _gantry, Vector3.zero, Vector3.one, paint), from, to, thickness);

        /// <summary>Stretches a unit cube (child of the gantry) between two gantry-local points.</summary>
        private static void Beam(Transform beam, Vector3 from, Vector3 to, float thickness)
        {
            var direction = to - from;
            var length = direction.magnitude;
            beam.localPosition = (from + to) / 2f;
            beam.localRotation = length > 1e-4f ? Quaternion.LookRotation(direction, Mathf.Abs(direction.normalized.y) > 0.99f ? Vector3.forward : Vector3.up) : Quaternion.identity;
            beam.localScale = new Vector3(thickness, thickness, Mathf.Max(0.01f, length));
        }

        private Transform Box(string name, Transform parent, Vector3 localPosition, Vector3 size, string paint)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(part.GetComponent<Collider>());
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = size;
            part.GetComponent<Renderer>().sharedMaterial = _palette.Get(paint);
            return part.transform;
        }

        private void Cylinder(string name, Transform parent, Vector3 localPosition, Vector3 axis, float radius, float length, string paint)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(part.GetComponent<Collider>());
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localRotation = Quaternion.FromToRotation(Vector3.up, axis);
            part.transform.localScale = new Vector3(radius * 2f, length / 2f, radius * 2f);
            part.GetComponent<Renderer>().sharedMaterial = _palette.Get(paint);
        }
    }
}
