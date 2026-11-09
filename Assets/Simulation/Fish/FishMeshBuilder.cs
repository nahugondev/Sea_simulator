using System.Collections.Generic;
using UnityEngine;

namespace SeaSim.Fish
{
    /// <summary>
    /// Builds a small, low-poly fish mesh procedurally (no external asset needed).
    /// The fish points along +Z, is 1 unit long and is centred on the origin.
    /// UV0.x = normalised position along the body (0 = nose, 1 = tail tip), used by the swim shader.
    /// UV0.y = normalised height (-1 = belly, +1 = back), used for counter-shading.
    /// </summary>
    public static class FishMeshBuilder
    {
        const float BodyEnd = 0.86f;      // where the body stops and the caudal fin starts (in s)
        const float HalfHeight = 0.115f;  // body half-height
        const float HalfWidth = 0.055f;   // body half-width (fish are laterally compressed)

        public static Mesh Build(int lengthSegments = 14, int radialSegments = 10)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            // ---- Body: a lofted ellipse whose radius follows a streamlined profile ----
            int ringSize = radialSegments + 1;
            for (int i = 0; i <= lengthSegments; i++)
            {
                float s = BodyEnd * i / lengthSegments;
                float z = 0.5f - s;
                float profile = BodyProfile(s);
                for (int j = 0; j <= radialSegments; j++)
                {
                    float a = (float)j / radialSegments * Mathf.PI * 2f;
                    float cx = Mathf.Sin(a), cy = Mathf.Cos(a);
                    vertices.Add(new Vector3(cx * HalfWidth * profile, cy * HalfHeight * profile, z));
                    // Normal of an ellipse, slightly tilted forward/back according to the profile slope.
                    float slope = (BodyProfile(Mathf.Min(s + 0.01f, BodyEnd)) - BodyProfile(Mathf.Max(s - 0.01f, 0f))) / 0.02f;
                    var n = new Vector3(cx / HalfWidth, cy / HalfHeight, slope * 1.2f).normalized;
                    normals.Add(n);
                    uvs.Add(new Vector2(s, cy));
                }
            }
            for (int i = 0; i < lengthSegments; i++)
            for (int j = 0; j < radialSegments; j++)
            {
                int a = i * ringSize + j, b = a + ringSize;
                triangles.AddRange(new[] { a, a + 1, b, a + 1, b + 1, b });
            }

            // ---- Caudal (tail) fin: forked, flat, double-sided ----
            float zRoot = 0.5f - (BodyEnd - 0.03f);
            float zTip = -0.5f;
            float zFork = 0.5f - 0.94f;
            AddFin(vertices, normals, uvs, triangles, new[]
            {
                new Vector3(0, HalfHeight * 0.18f, zRoot),
                new Vector3(0, HalfHeight * 1.25f, zTip - 0.02f),
                new Vector3(0, 0, zFork),
                new Vector3(0, -HalfHeight * 1.15f, zTip),
                new Vector3(0, -HalfHeight * 0.18f, zRoot),
            });

            // ---- Small dorsal fin ----
            AddFin(vertices, normals, uvs, triangles, new[]
            {
                new Vector3(0, HalfHeight * 0.92f, 0.08f),
                new Vector3(0, HalfHeight * 1.55f, -0.04f),
                new Vector3(0, HalfHeight * 0.80f, -0.12f),
            });

            var mesh = new Mesh { name = "ProceduralFish" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            // Fish bend at runtime in the shader: pad the bounds so they are never culled incorrectly.
            var b2 = mesh.bounds;
            b2.Expand(0.2f);
            mesh.bounds = b2;
            return mesh;
        }

        /// <summary>Thickness of the body (0..1) along s, from nose (0) to tail root.</summary>
        static float BodyProfile(float s)
        {
            if (s < 0.28f)
                return Mathf.Sqrt(Mathf.Sin(s / 0.28f * Mathf.PI * 0.5f)) * 0.98f + 0.02f; // rounded head
            float t = Mathf.InverseLerp(0.28f, BodyEnd, s);
            t = t * t * (3f - 2f * t);
            return Mathf.Lerp(1f, 0.14f, t); // taper toward the caudal peduncle
        }

        /// <summary>Adds a flat polygon (in the YZ plane) as a triangle fan, with both faces.</summary>
        static void AddFin(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> tris, Vector3[] outline)
        {
            for (int side = 0; side < 2; side++)
            {
                int start = v.Count;
                var normal = side == 0 ? Vector3.right : Vector3.left;
                foreach (var p in outline)
                {
                    v.Add(p);
                    n.Add(normal);
                    uv.Add(new Vector2(Mathf.Clamp01(0.5f - p.z), Mathf.Clamp(p.y / HalfHeight, -1f, 1f)));
                }
                for (int i = 1; i < outline.Length - 1; i++)
                {
                    // Outlines are given counter-clockwise as seen from +X; Unity front faces are clockwise.
                    if (side == 0) tris.AddRange(new[] { start, start + i + 1, start + i });
                    else tris.AddRange(new[] { start, start + i, start + i + 1 });
                }
            }
        }
    }
}
