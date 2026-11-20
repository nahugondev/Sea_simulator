using NUnit.Framework;
using SeaSim.Fish;
using UnityEngine;

namespace SeaSim.Tests
{
    public class FishMeshBuilderTests
    {
        GameObject model;

        [TearDown]
        public void TearDown()
        {
            if (model == null) return;
            var filter = model.GetComponent<MeshFilter>();
            if (filter != null) Object.DestroyImmediate(filter.sharedMesh);
            Object.DestroyImmediate(model);
        }

        /// <summary>A 4 m long wedge pointing along -X (thick at the nose), offset from the origin, like a badly exported model.</summary>
        GameObject MakeModel()
        {
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(10f, 5f, 0f),             // tail tip, at +X
                    new Vector3(6f, 5.5f, 0.3f),          // nose, at -X, raised (back up = +Y)
                    new Vector3(6f, 4.5f, -0.3f),
                },
                triangles = new[] { 0, 1, 2 },
            };
            model = new GameObject("Test model");
            model.AddComponent<MeshFilter>().sharedMesh = mesh;
            return model;
        }

        [Test]
        public void FromModel_IsOneUnitLongAndCentred()
        {
            var mesh = FishMeshBuilder.FromModel(MakeModel(), Vector3.left, Vector3.up);
            try
            {
                Assert.AreEqual(1f, mesh.bounds.size.z, 1e-4f);
                Assert.AreEqual(0f, mesh.bounds.center.x, 1e-4f);
                Assert.AreEqual(0f, mesh.bounds.center.y, 1e-4f);
                Assert.AreEqual(0f, mesh.bounds.center.z, 1e-4f);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void FromModel_PutsTheNoseAlongPlusZ()
        {
            var mesh = FishMeshBuilder.FromModel(MakeModel(), Vector3.left, Vector3.up);
            try
            {
                var v = mesh.vertices;
                Assert.AreEqual(-0.5f, v[0].z, 1e-4f, "tail");
                Assert.AreEqual(0.5f, v[1].z, 1e-4f, "nose");
                Assert.Greater(v[1].y, v[2].y, "the model's up must stay up");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        /// <summary>The shadow mesh (a lower level of detail) must land exactly where the full mesh does, with fewer triangles.</summary>
        [Test]
        public void FromModel_LodSharesTheFrameWithFewerTriangles()
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var mesh = Object.Instantiate(sphere.GetComponent<MeshFilter>().sharedMesh);
            Object.DestroyImmediate(sphere);
            mesh.vertices = System.Array.ConvertAll(mesh.vertices, v => new Vector3(v.x * 0.3f, v.y * 0.6f, v.z * 2f));
            UnityEditor.MeshLodUtility.GenerateMeshLods(mesh, default, 8);
            Assume.That(mesh.lodCount, Is.GreaterThan(2));
            model = new GameObject("Model with LODs");
            model.AddComponent<MeshFilter>().sharedMesh = mesh;

            var full = FishMeshBuilder.FromModel(model, Vector3.forward, Vector3.up);
            var light = FishMeshBuilder.FromModel(model, Vector3.forward, Vector3.up, 2);
            try
            {
                Assert.Less(light.triangles.Length, full.triangles.Length / 2);
                Assert.AreEqual(full.bounds.center, light.bounds.center);
                Assert.AreEqual(full.bounds.size, light.bounds.size);
            }
            finally
            {
                Object.DestroyImmediate(full);
                Object.DestroyImmediate(light);
            }
        }

        [Test]
        public void FromModel_ReturnsNullWithoutMesh()
        {
            model = new GameObject("No mesh filter");
            Assert.IsNull(FishMeshBuilder.FromModel(model, Vector3.forward, Vector3.up));
        }
    }
}
