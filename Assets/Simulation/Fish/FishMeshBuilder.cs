using System.Collections.Generic;
using UnityEngine;

namespace SeaSim.Fish
{
    /// <summary>Silhouette of a procedural fish. All values are fractions of the fish length.</summary>
    [System.Serializable]
    public struct FishBodyShape
    {
        [Tooltip("Half-height of the body.")]
        [Range(0.03f, 0.3f)] public float halfHeight;
        [Tooltip("Half-width of the body (fish are laterally compressed).")]
        [Range(0.02f, 0.2f)] public float halfWidth;
        [Tooltip("Where the body ends and the caudal (tail) fin starts, from the nose (0) to the tail tip (1).")]
        [Range(0.6f, 0.95f)] public float bodyEnd;
        [Tooltip("Height of the tail fin relative to the body height.")]
        [Range(0.5f, 2.5f)] public float tailSize;
        [Tooltip("Size of the dorsal fin (0 = none).")]
        [Range(0f, 2f)] public float dorsalFinSize;

        public static FishBodyShape Sardine => new FishBodyShape
        {
            halfHeight = 0.115f, halfWidth = 0.055f, bodyEnd = 0.86f, tailSize = 1.2f, dorsalFinSize = 1f,
        };

        /// <summary>Spindle-shaped, rounder in section than a sardine, with a large forked tail.</summary>
        public static FishBodyShape Mackerel => new FishBodyShape
        {
            halfHeight = 0.085f, halfWidth = 0.062f, bodyEnd = 0.87f, tailSize = 1.6f, dorsalFinSize = 0.8f,
        };

        public static FishBodyShape Predator => new FishBodyShape
        {
            halfHeight = 0.075f, halfWidth = 0.05f, bodyEnd = 0.88f, tailSize = 1.9f, dorsalFinSize = 1.4f,
        };
    }

    /// <summary>
    /// Builds a small, low-poly fish mesh procedurally (no external asset needed).
    /// The fish points along +Z, is 1 unit long and is centred on the origin.
    /// UV0.x = normalised position along the body (0 = nose, 1 = tail tip; the swim shader reads it from z instead).
    /// UV0.y = normalised height (-1 = belly, +1 = back), used for counter-shading.
    /// </summary>
    public static class FishMeshBuilder
    {
        /// <summary>The species' mesh: its 3D model when one is set, otherwise a generated body.</summary>
        public static Mesh Build(FishSpecies species)
        {
            if (species.model != null)
            {
                var mesh = FromModel(species.model, species.modelForward, species.modelUp);
                if (mesh != null) return mesh;
                Debug.LogWarning($"{species.name}: the model has no readable mesh, using the generated body instead.", species);
            }
            return Build(species.bodyShape);
        }

        /// <summary>
        /// Copies a model's mesh into the same frame as the generated fish, so the swim shader treats both alike:
        /// nose along +Z, back along +Y, centred on the origin, 1 unit long (z from -0.5 at the tail to +0.5 at the
        /// nose). The model's own UVs are kept for its texture. All submeshes are merged into one.
        /// </summary>
        public static Mesh FromModel(GameObject model, Vector3 modelForward, Vector3 modelUp)
        {
            var filter = model.GetComponentInChildren<MeshFilter>(true);
            var source = filter != null ? filter.sharedMesh : null;
            if (source == null || !source.isReadable) return null;

            var toFish = Quaternion.Inverse(Quaternion.LookRotation(modelForward, modelUp));
            var vertices = source.vertices;
            var normals = source.normals;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = toFish * vertices[i];
            for (int i = 0; i < normals.Length; i++) normals[i] = toFish * normals[i];

            var bounds = GeometryUtility.CalculateBounds(vertices, Matrix4x4.identity);
            float scale = 1f / Mathf.Max(bounds.size.z, 1e-5f);
            for (int i = 0; i < vertices.Length; i++) vertices[i] = (vertices[i] - bounds.center) * scale;

            var triangles = new List<int>();
            for (int s = 0; s < source.subMeshCount; s++) triangles.AddRange(source.GetTriangles(s));

            var mesh = new Mesh { name = model.name + " (fish)" };
            if (vertices.Length > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            if (normals.Length == vertices.Length) mesh.SetNormals(normals);
            mesh.SetUVs(0, source.uv);
            mesh.SetTriangles(triangles, 0);
            if (normals.Length != vertices.Length) mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        public static Mesh Build(FishBodyShape shape, int lengthSegments = 14, int radialSegments = 10)
        {
            float bodyEnd = shape.bodyEnd;
            float halfHeight = shape.halfHeight;
            float halfWidth = shape.halfWidth;

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            // ---- Body: a lofted ellipse whose radius follows a streamlined profile ----
            int ringSize = radialSegments + 1;
            for (int i = 0; i <= lengthSegments; i++)
            {
                float s = bodyEnd * i / lengthSegments;
                float z = 0.5f - s;
                float profile = BodyProfile(s, bodyEnd);
                float slope = (BodyProfile(Mathf.Min(s + 0.01f, bodyEnd), bodyEnd) - BodyProfile(Mathf.Max(s - 0.01f, 0f), bodyEnd)) / 0.02f;
                for (int j = 0; j <= radialSegments; j++)
                {
                    float a = (float)j / radialSegments * Mathf.PI * 2f;
                    float cx = Mathf.Sin(a), cy = Mathf.Cos(a);
                    vertices.Add(new Vector3(cx * halfWidth * profile, cy * halfHeight * profile, z));
                    // Normal of an ellipse, slightly tilted forward/back according to the profile slope.
                    normals.Add(new Vector3(cx / halfWidth, cy / halfHeight, slope * 1.2f).normalized);
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
            float zRoot = 0.5f - (bodyEnd - 0.03f);
            float zTip = -0.5f;
            float zFork = 0.5f - (bodyEnd + (1f - bodyEnd) * 0.55f);
            float tail = halfHeight * shape.tailSize;
            AddFin(vertices, normals, uvs, triangles, halfHeight, new[]
            {
                new Vector3(0, halfHeight * 0.18f, zRoot),
                new Vector3(0, tail * 1.05f, zTip - 0.02f),
                new Vector3(0, 0, zFork),
                new Vector3(0, -tail, zTip),
                new Vector3(0, -halfHeight * 0.18f, zRoot),
            });

            // ---- Dorsal fin ----
            if (shape.dorsalFinSize > 0f)
            {
                float d = shape.dorsalFinSize;
                AddFin(vertices, normals, uvs, triangles, halfHeight, new[]
                {
                    new Vector3(0, halfHeight * 0.92f, 0.08f),
                    new Vector3(0, halfHeight * (0.92f + 0.63f * d), -0.04f),
                    new Vector3(0, halfHeight * 0.80f, 0.08f - 0.2f * d),
                });
            }

            var mesh = new Mesh { name = "ProceduralFish" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            // Fish bend at runtime in the shader: pad the bounds so they are never culled incorrectly.
            var bounds = mesh.bounds;
            bounds.Expand(0.2f);
            mesh.bounds = bounds;
            return mesh;
        }

        /// <summary>Thickness of the body (0..1) along s, from nose (0) to tail root.</summary>
        static float BodyProfile(float s, float bodyEnd)
        {
            if (s < 0.28f)
                return Mathf.Sqrt(Mathf.Sin(s / 0.28f * Mathf.PI * 0.5f)) * 0.98f + 0.02f; // rounded head
            float t = Mathf.InverseLerp(0.28f, bodyEnd, s);
            t = t * t * (3f - 2f * t);
            return Mathf.Lerp(1f, 0.14f, t); // taper toward the caudal peduncle
        }

        /// <summary>Adds a flat polygon (in the YZ plane) as a triangle fan, with both faces.</summary>
        static void AddFin(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> tris, float halfHeight, Vector3[] outline)
        {
            for (int side = 0; side < 2; side++)
            {
                int start = v.Count;
                var normal = side == 0 ? Vector3.right : Vector3.left;
                foreach (var p in outline)
                {
                    v.Add(p);
                    n.Add(normal);
                    uv.Add(new Vector2(Mathf.Clamp01(0.5f - p.z), Mathf.Clamp(p.y / halfHeight, -1f, 1f)));
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
