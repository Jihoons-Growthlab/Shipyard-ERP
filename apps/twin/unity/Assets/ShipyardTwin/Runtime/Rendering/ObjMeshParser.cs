using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShipyardTwin
{
    /// <summary>
    /// Wavefront OBJ reader for generated models: <c>v</c>, <c>vt</c>, <c>vn</c>, <c>f</c> and <c>usemtl</c> lines
    /// (polygons are fanned, negative indices supported). Each material name becomes one submesh in order of first
    /// use; faces before any <c>usemtl</c> use the name "". Converts OBJ axes to Unity
    /// (<see cref="ShipCoordinates.ObjToUnity"/>) and reverses the winding to match.
    /// <para>
    /// Files with normals or texture coordinates (Blender exports) get one vertex per distinct v/vt/vn triple, so
    /// hard and smooth edges and UVs are kept. Files with positions only (the generated block models) keep shared
    /// positions; their plates are double-sided, so a back face gets its own vertex copies and recalculated normals
    /// do not cancel out. Meshes without UVs get box-projected UVs in metres for tiling detail textures.
    /// </para>
    /// </summary>
    public static class ObjMeshParser
    {
        public readonly struct Result
        {
            public Result(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<string> materials, List<List<int>> submeshes)
            {
                Vertices = vertices;
                Normals = normals;
                Uvs = uvs;
                Materials = materials;
                Submeshes = submeshes;
            }

            public List<Vector3> Vertices { get; }

            /// <summary>Per-vertex normals from the file, or null (recalculated).</summary>
            public List<Vector3> Normals { get; }

            /// <summary>Per-vertex UVs from the file, or null (box-projected in metres).</summary>
            public List<Vector2> Uvs { get; }

            /// <summary>Material name per submesh.</summary>
            public List<string> Materials { get; }

            public List<List<int>> Submeshes { get; }

            /// <summary>All triangles, submeshes concatenated.</summary>
            public List<int> Triangles
            {
                get
                {
                    var all = new List<int>();
                    foreach (var submesh in Submeshes)
                    {
                        all.AddRange(submesh);
                    }

                    return all;
                }
            }
        }

        public static Result Parse(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            var positions = new List<Vector3>();
            var fileUvs = new List<Vector2>();
            var fileNormals = new List<Vector3>();
            var faces = new List<(int Submesh, List<(int V, int T, int N)> Corners)>();
            var materials = new List<string>();
            var submeshes = new List<List<int>>();
            var current = -1;
            var lineNumber = 0;
            foreach (var raw in text.Split('\n'))
            {
                lineNumber++;
                var line = raw.Trim();
                if (line.StartsWith("usemtl", StringComparison.Ordinal))
                {
                    current = Submesh(materials, submeshes, line.Length > 6 ? line.Substring(6).Trim() : string.Empty);
                }
                else if (line.StartsWith("v ", StringComparison.Ordinal))
                {
                    var p = Numbers(line, 3, lineNumber);
                    positions.Add(ShipCoordinates.ObjToUnity(p[0], p[1], p[2]));
                }
                else if (line.StartsWith("vt ", StringComparison.Ordinal))
                {
                    var t = Numbers(line, 2, lineNumber);
                    fileUvs.Add(new Vector2(t[0], t[1]));
                }
                else if (line.StartsWith("vn ", StringComparison.Ordinal))
                {
                    var n = Numbers(line, 3, lineNumber);
                    fileNormals.Add(ShipCoordinates.ObjToUnity(n[0], n[1], n[2]));
                }
                else if (line.StartsWith("f ", StringComparison.Ordinal))
                {
                    var corners = new List<(int, int, int)>();
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 1; i < parts.Length; i++)
                    {
                        var fields = parts[i].Split('/');
                        corners.Add((Index(fields[0], positions.Count, lineNumber, true),
                            // vt/vn indices only count when the file has such data (generated plates may carry bare v/v pairs).
                            fields.Length > 1 && fields[1].Length > 0 && fileUvs.Count > 0 ? Index(fields[1], fileUvs.Count, lineNumber, true) : -1,
                            fields.Length > 2 && fields[2].Length > 0 && fileNormals.Count > 0 ? Index(fields[2], fileNormals.Count, lineNumber, true) : -1));
                    }

                    if (current < 0)
                    {
                        current = Submesh(materials, submeshes, string.Empty);
                    }

                    faces.Add((current, corners));
                }
            }

            var indexed = fileNormals.Count > 0 || fileUvs.Count > 0;
            return indexed ? BuildIndexed(positions, fileUvs, fileNormals, faces, materials, submeshes)
                           : BuildShared(positions, faces, materials, submeshes);
        }

        private static Result BuildIndexed(List<Vector3> positions, List<Vector2> fileUvs, List<Vector3> fileNormals,
            List<(int Submesh, List<(int V, int T, int N)> Corners)> faces, List<string> materials, List<List<int>> submeshes)
        {
            var vertices = new List<Vector3>();
            var normals = fileNormals.Count > 0 ? new List<Vector3>() : null;
            var uvs = fileUvs.Count > 0 ? new List<Vector2>() : null;
            var map = new Dictionary<(int, int, int), int>();
            int Vertex((int V, int T, int N) corner)
            {
                if (!map.TryGetValue(corner, out var index))
                {
                    index = vertices.Count;
                    vertices.Add(positions[corner.V]);
                    normals?.Add(corner.N >= 0 ? fileNormals[corner.N] : Vector3.up);
                    uvs?.Add(corner.T >= 0 ? fileUvs[corner.T] : Vector2.zero);
                    map[corner] = index;
                }

                return index;
            }

            foreach (var (submesh, corners) in faces)
            {
                for (var i = 1; i + 1 < corners.Count; i++)
                {
                    // Reversed winding (a, c, b): the axis conversion mirrors handedness.
                    submeshes[submesh].Add(Vertex(corners[0]));
                    submeshes[submesh].Add(Vertex(corners[i + 1]));
                    submeshes[submesh].Add(Vertex(corners[i]));
                }
            }

            return new Result(vertices, normals, uvs, materials, submeshes);
        }

        private static Result BuildShared(List<Vector3> positions, List<(int Submesh, List<(int V, int T, int N)> Corners)> faces,
            List<string> materials, List<List<int>> submeshes)
        {
            var vertices = new List<Vector3>(positions);
            var seen = new HashSet<(int, int, int)>();
            foreach (var (submesh, corners) in faces)
            {
                for (var i = 1; i + 1 < corners.Count; i++)
                {
                    AddTriangle(vertices, submeshes[submesh], seen, corners[0].V, corners[i + 1].V, corners[i].V);
                }
            }

            return new Result(vertices, null, null, materials, submeshes);
        }

        private static int Submesh(List<string> materials, List<List<int>> submeshes, string name)
        {
            var index = materials.IndexOf(name);
            if (index < 0)
            {
                materials.Add(name);
                submeshes.Add(new List<int>());
                index = materials.Count - 1;
            }

            return index;
        }

        private static void AddTriangle(List<Vector3> vertices, List<int> triangles, HashSet<(int, int, int)> seen,
            int a, int b, int c)
        {
            var key = Sorted(a, b, c);
            if (!seen.Add(key))
            {
                // Same three vertices with the other winding: copy them so this face keeps its own normal.
                var copy = vertices.Count;
                vertices.Add(vertices[a]);
                vertices.Add(vertices[b]);
                vertices.Add(vertices[c]);
                a = copy;
                b = copy + 1;
                c = copy + 2;
            }

            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
        }

        private static (int, int, int) Sorted(int a, int b, int c)
        {
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            return (a, b, c);
        }

        private static int Index(string token, int count, int line, bool required)
        {
            var index = int.Parse(token, CultureInfo.InvariantCulture);
            index = index < 0 ? count + index : index - 1;
            if (index < 0 || index >= count)
            {
                throw new FormatException("OBJ line " + line + ": face index out of range.");
            }

            return index;
        }

        private static float[] Numbers(string line, int count, int lineNumber)
        {
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < count + 1)
            {
                throw new FormatException("OBJ line " + lineNumber + ": expected " + count + " numbers.");
            }

            var values = new float[count];
            for (var i = 0; i < count; i++)
            {
                if (!float.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                {
                    throw new FormatException("OBJ line " + lineNumber + ": '" + parts[i + 1] + "' is not a number.");
                }
            }

            return values;
        }

        public static Mesh ToMesh(Result result, string name)
        {
            var mesh = new Mesh { name = name };
            if (result.Vertices.Count > 65535)
            {
                mesh.indexFormat = IndexFormat.UInt32;
            }

            mesh.SetVertices(result.Vertices);
            mesh.subMeshCount = Math.Max(1, result.Submeshes.Count);
            for (var i = 0; i < result.Submeshes.Count; i++)
            {
                mesh.SetTriangles(result.Submeshes[i], i);
            }

            if (result.Normals != null)
            {
                mesh.SetNormals(result.Normals);
            }
            else
            {
                mesh.RecalculateNormals();
            }

            mesh.SetUVs(0, result.Uvs ?? BoxUvs(result.Vertices, result.Submeshes));
            if (mesh.GetTopology(0) == MeshTopology.Triangles)
            {
                mesh.RecalculateTangents();
            }

            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>UVs in metres projected along each triangle's dominant axis (first triangle wins per vertex).</summary>
        public static List<Vector2> BoxUvs(List<Vector3> vertices, List<List<int>> submeshes)
        {
            var uvs = new Vector2[vertices.Count];
            var done = new bool[vertices.Count];
            foreach (var triangles in submeshes)
            {
                for (var i = 0; i + 2 < triangles.Count; i += 3)
                {
                    int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                    var n = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                    n = new Vector3(Mathf.Abs(n.x), Mathf.Abs(n.y), Mathf.Abs(n.z));
                    foreach (var v in new[] { a, b, c })
                    {
                        if (done[v])
                        {
                            continue;
                        }

                        var p = vertices[v];
                        uvs[v] = n.x >= n.y && n.x >= n.z ? new Vector2(p.z, p.y) : n.y >= n.z ? new Vector2(p.x, p.z) : new Vector2(p.x, p.y);
                        done[v] = true;
                    }
                }
            }

            return new List<Vector2>(uvs);
        }

        /// <summary>Box mesh used when a block has no model.</summary>
        public static Mesh Box(Bounds bounds, string name)
        {
            var mesh = new Mesh { name = name };
            var min = bounds.min;
            var max = bounds.max;
            var corners = new[]
            {
                new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z), new Vector3(max.x, max.y, min.z),
                new Vector3(min.x, max.y, min.z), new Vector3(min.x, min.y, max.z), new Vector3(max.x, min.y, max.z),
                new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z),
            };
            int[][] faces =
            {
                new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 },
                new[] { 3, 7, 6, 2 }, new[] { 0, 4, 7, 3 }, new[] { 1, 2, 6, 5 },
            };
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            foreach (var f in faces)
            {
                var start = vertices.Count;
                foreach (var corner in f)
                {
                    vertices.Add(corners[corner]);
                }

                triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            }

            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.SetUVs(0, BoxUvs(vertices, new List<List<int>> { triangles }));
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
