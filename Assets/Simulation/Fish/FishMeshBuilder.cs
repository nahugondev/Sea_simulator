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
        /// <summary>
        /// The species' mesh, from its 3D model, at the given level of detail (0 = full model). Null (with an error)
        /// when the model is missing or unreadable.
        /// </summary>
        public static Mesh Build(FishSpecies species, int lod = 0)
        {
            var mesh = species.model != null ? FromModel(species.model, species.modelForward, species.modelUp, lod) : null;
            if (mesh == null)
                Debug.LogError($"{species.name}: no model, or its mesh is not readable (enable Read/Write in the model's import settings).", species);
            return mesh;
        }

        /// <summary>
        /// Number of levels of detail of a model's mesh: 1 unless "Generate Mesh LODs" is enabled in its import
        /// settings, in which case Unity stores simplified versions of the mesh (fewer triangles, same vertices).
        /// </summary>
        public static int LodCount(GameObject model)
        {
            var filter = model != null ? model.GetComponentInChildren<MeshFilter>(true) : null;
            return filter != null && filter.sharedMesh != null ? filter.sharedMesh.lodCount : 0;
        }

        /// <summary>
        /// Copies a model's mesh into the same frame as the generated fish, so the swim shader treats both alike:
        /// nose along +Z, back along +Y, centred on the origin, 1 unit long (z from -0.5 at the tail to +0.5 at the
        /// nose). The model's own UVs are kept for its texture. All submeshes are merged into one.
        /// <paramref name="lod"/> picks one of the model's levels of detail (clamped to those it has); every level
        /// shares the same vertices, so all of them land in exactly the same frame.
        /// </summary>
        public static Mesh FromModel(GameObject model, Vector3 modelForward, Vector3 modelUp, int lod = 0)
        {
            var filter = model.GetComponentInChildren<MeshFilter>(true);
            var source = filter != null ? filter.sharedMesh : null;
            if (source == null || !source.isReadable) return null;
            lod = Mathf.Clamp(lod, 0, source.lodCount - 1);

            var toFish = Quaternion.Inverse(Quaternion.LookRotation(modelForward, modelUp));
            var vertices = source.vertices;
            var normals = source.normals;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = toFish * vertices[i];
            for (int i = 0; i < normals.Length; i++) normals[i] = toFish * normals[i];

            var bounds = GeometryUtility.CalculateBounds(vertices, Matrix4x4.identity);
            float scale = 1f / Mathf.Max(bounds.size.z, 1e-5f);
            for (int i = 0; i < vertices.Length; i++) vertices[i] = (vertices[i] - bounds.center) * scale;

            var triangles = new List<int>();
            for (int s = 0; s < source.subMeshCount; s++) triangles.AddRange(source.GetIndices(s, lod, true));

            var mesh = new Mesh { name = model.name + (lod > 0 ? $" (fish LOD{lod})" : " (fish)") };
            if (vertices.Length > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            if (normals.Length == vertices.Length) mesh.SetNormals(normals);
            mesh.SetUVs(0, source.uv);
            mesh.SetTriangles(triangles, 0);
            if (normals.Length != vertices.Length) mesh.RecalculateNormals();
            // Reorder triangles for the GPU's vertex cache: thousands of instances make the order matter, and
            // importing a model with mesh LODs leaves its full-detail triangles in a slower order (measured ~0.4 ms
            // at 20k fish).
            mesh.OptimizeIndexBuffers();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
