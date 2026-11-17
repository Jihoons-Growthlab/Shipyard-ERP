using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Block poses (world space, pivot = centre of gravity, block mesh in ship axes): in a yard slot, landed on the
    /// hull, and along the erection lift from the slot to the hull. The goliath of the hull's dock only reaches over
    /// its rails (the dock and the PE yard at its head); a block stocked elsewhere is first driven on the ground to
    /// the pick-up point at the rail head, then hoisted, carried over and lowered.
    /// </summary>
    public static class BlockMotion
    {
        /// <summary>Supports under a stocked block (m).</summary>
        public const float Dunnage = 0.3f;

        /// <summary>Clearance above the higher of start and landing while carried (m).</summary>
        public const float CarryClearance = 30f;

        /// <summary>Lift progress spent driving to the rail head when the slot is out of the crane's reach.</summary>
        public const float GroundShare = 0.3f;

        public readonly struct Reach
        {
            public Reach(Rect rails, Vector2 pickup)
            {
                Rails = rails;
                Pickup = pickup;
            }

            /// <summary>Yard-metre rectangle the hook can reach (x along the dock, y across).</summary>
            public Rect Rails { get; }

            /// <summary>Where a transporter sets a block down for the crane (yard metres): the PE yard at the rail head.</summary>
            public Vector2 Pickup { get; }

            public bool Contains(Vector2 yard) => Rails.Contains(yard);
        }

        /// <summary>Crane reach of a dock (GantryCraneView rails: 100 m past the head, to 10 m short of the gate, 8 m beyond the walls).</summary>
        public static Reach CraneReach(Facility dock)
        {
            var pit = dock.size_x_m / 2f - YardLayout.WallThickness;
            float x0 = dock.x - pit - 100f, x1 = dock.x + pit - 10f;
            var half = dock.size_y_m / 2f + 8f - 9f;   // the trolley stops 9 m inside the rails
            return new Reach(Rect.MinMaxRect(x0, dock.y - half, x1, dock.y + half), new Vector2(dock.x - pit - 50f, dock.y));
        }

        public static Pose Landed(HullPlacement placement, BlockRecord block)
        {
            var hull = ShipCoordinates.HullPose(placement);
            return new Pose(hull.position + hull.rotation * ShipCoordinates.CentreOfGravity(block), hull.rotation);
        }

        /// <summary>Resting on its bottom in the slot, ship x along the slot heading.</summary>
        public static Pose InYard(BlockRecord block, float x, float y, float headingDeg) =>
            new Pose(ShipCoordinates.YardToUnity(x, y, YardLayout.QuayLevel + Dunnage + (block.cog_z - block.z_min)),
                ShipCoordinates.HeadingToUnity(headingDeg));

        public static Pose InYard(BlockRecord block, StockStay stay) => InYard(block, stay.Row.x, stay.Row.y, stay.Row.heading_deg);

        public static Vector2 YardOf(Vector3 world) => new Vector2(world.x, world.z);

        /// <summary>
        /// Pose at lift progress p (0..1) and whether the crane holds it (false while driven on the ground). With no
        /// slot the block comes in from the dock side at hook height, as before stocking plans existed.
        /// </summary>
        public static (Pose Pose, bool Hooked) Lift(Pose? slot, Pose landed, float p, Reach reach)
        {
            p = Mathf.Clamp01(p);
            if (slot == null)
            {
                var side = Vector3.back * 48f;
                const float height = 55f;
                if (p < 0.5f)
                {
                    var t = Mathf.SmoothStep(0f, 1f, p * 2f);
                    return (new Pose(landed.position + Vector3.up * height + side * (1f - t), landed.rotation), true);
                }

                return (new Pose(landed.position + Vector3.up * (height * (1f - Mathf.SmoothStep(0f, 1f, (p - 0.5f) * 2f))), landed.rotation), true);
            }

            var start = slot.Value;
            var ground = reach.Contains(YardOf(start.position)) ? 0f : GroundShare;
            if (ground > 0f)
            {
                var pickup = new Vector3(reach.Pickup.x, start.position.y, reach.Pickup.y);
                if (p < ground)
                {
                    var t = Mathf.SmoothStep(0f, 1f, p / ground);
                    return (new Pose(Vector3.Lerp(start.position, pickup, t), start.rotation), false);
                }

                start = new Pose(pickup, start.rotation);
            }

            var q = (p - ground) / (1f - ground);
            var top = Mathf.Max(start.position.y, landed.position.y) + CarryClearance;
            var above = new Vector3(start.position.x, top, start.position.z);
            var over = new Vector3(landed.position.x, top, landed.position.z);
            if (q < 0.2f)
            {
                return (new Pose(Vector3.Lerp(start.position, above, Mathf.SmoothStep(0f, 1f, q / 0.2f)), start.rotation), true);
            }

            if (q < 0.75f)
            {
                var t = Mathf.SmoothStep(0f, 1f, (q - 0.2f) / 0.55f);
                return (new Pose(Vector3.Lerp(above, over, t), Quaternion.Slerp(start.rotation, landed.rotation, t)), true);
            }

            return (new Pose(Vector3.Lerp(over, landed.position, Mathf.SmoothStep(0f, 1f, (q - 0.75f) / 0.25f)), landed.rotation), true);
        }
    }
}
