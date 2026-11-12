using System.Collections.Generic;
using UnityEngine;

namespace SeaSim.Fish
{
    /// <summary>
    /// Fish meshes, all in the same frame so the swim shader treats them alike: the fish points along +Z, is 1 unit
    /// long and is centred on the origin.
    /// Species and the predator all use their 3D model (<see cref="FromModel"/>).
    /// </summary>
    public static class FishMeshBuilder
    {
        /// <summary>The species' mesh, from its 3D model. Null (with an error) when the model is missing or unreadable.</summary>
        public static Mesh Build(FishSpecies species)
        {
            var mesh = species.model != null ? FromModel(species.model, species.modelForward, species.modelUp) : null;
            if (mesh == null)
                Debug.LogError($"{species.name}: no model, or its mesh is not readable (enable Read/Write in the model's import settings).", species);
            return mesh;
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
    }
}
