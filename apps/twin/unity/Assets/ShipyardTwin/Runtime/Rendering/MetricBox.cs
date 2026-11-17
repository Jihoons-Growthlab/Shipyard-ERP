using System.Collections.Generic;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Box meshes of a given size with UVs in metres, so tiling detail textures keep their real scale (a scaled unit
    /// cube would stretch them). Meshes are shared per size.
    /// </summary>
    public static class MetricBox
    {
        private static readonly Dictionary<Vector3Int, Mesh> Cache = new Dictionary<Vector3Int, Mesh>();

        public static Mesh Get(Vector3 size)
        {
            var key = new Vector3Int(Mathf.RoundToInt(size.x * 100), Mathf.RoundToInt(size.y * 100), Mathf.RoundToInt(size.z * 100));
            if (!Cache.TryGetValue(key, out var mesh))
            {
                mesh = ObjMeshParser.Box(new Bounds(Vector3.zero, size), "Box " + size);
                Cache[key] = mesh;
            }

            return mesh;
        }

        /// <summary>A box GameObject (no collider) with a shared metric mesh and one material.</summary>
        public static GameObject Create(string name, Transform parent, Vector3 size, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = Get(size);
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }
    }
}
