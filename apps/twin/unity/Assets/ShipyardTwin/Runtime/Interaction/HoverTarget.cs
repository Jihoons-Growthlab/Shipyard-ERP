using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Name shown when the cursor rests on an object (<see cref="HoverTooltipView"/>). Sits on the object's root; the
    /// colliders on its child meshes find it with GetComponentInParent, so the nearest target wins (a block before
    /// the ship it belongs to). A target with an empty <see cref="Title"/> is an occluder: ground, sea, cranes —
    /// it stops the pick so nothing behind it is named.
    /// </summary>
    public sealed class HoverTarget : MonoBehaviour
    {
        public string Kind;
        public string Title;
        public string Detail;

        public bool IsOccluder => string.IsNullOrEmpty(Title);

        /// <summary>Adds (or updates) the target on root and a mesh collider on every child mesh without one.</summary>
        public static HoverTarget Attach(GameObject root, string kind, string title, string detail = null)
        {
            var target = root.GetComponent<HoverTarget>();
            if (target == null)
            {
                target = root.AddComponent<HoverTarget>();
            }

            target.Kind = kind;
            target.Title = title;
            target.Detail = detail;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh != null && filter.GetComponent<Collider>() == null)
                {
                    filter.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
                }
            }

            return target;
        }

        /// <summary>Ground, sea and machinery that hide what is behind them but have no name of their own.</summary>
        public static HoverTarget Occluder(GameObject root) => Attach(root, null, null);

        /// <summary>Facility label: the ERP name, else its ID.</summary>
        public static string FacilityTitle(Facility f) => string.IsNullOrWhiteSpace(f.name) ? f.location : f.name;
    }
}
