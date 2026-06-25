using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain.Tests
{
    public class MeshDataConversionTests
    {
        [Test]
        public void ToRenderMesh_ProducesValidMesh()
        {
            var data = TestMeshFactory.BuildPlane(2, 10f, Allocator.Temp);
            Mesh mesh = null;
            try
            {
                mesh = MeshDataConversions.ToRenderMesh(data, "plane");

                Assert.AreEqual(data.VertexCount, mesh.vertexCount);
                Assert.AreEqual(data.TriangleCount * 3, mesh.triangles.Length);
                Assert.AreEqual(data.VertexCount, mesh.uv.Length, "atlas UV channel should be populated");
                Assert.Greater(mesh.bounds.size.sqrMagnitude, 0f, "bounds should be non-degenerate");
            }
            finally
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
                data.Dispose();
            }
        }

        [Test]
        public void ToCollisionMesh_AssignableToMeshCollider()
        {
            var data = TestMeshFactory.BuildPlane(2, 10f, Allocator.Temp);
            Mesh mesh = null;
            GameObject go = null;
            try
            {
                mesh = MeshDataConversions.ToCollisionMesh(data, "plane_collision");
                Assert.AreEqual(data.VertexCount, mesh.vertexCount);

                go = new GameObject("collider");
                var collider = go.AddComponent<MeshCollider>();
                Assert.DoesNotThrow(() => collider.sharedMesh = mesh);
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
                if (mesh != null) Object.DestroyImmediate(mesh);
                data.Dispose();
            }
        }

        [Test]
        public void RoundTrip_PreservesGeometry()
        {
            var source = TestMeshFactory.BuildPlane(3, 9f, Allocator.Temp);
            Mesh mesh = null;
            MeshData restored = default;
            try
            {
                mesh = MeshDataConversions.ToRenderMesh(source);
                restored = MeshDataConversions.FromUnityMesh(mesh, Allocator.Temp);

                Assert.AreEqual(source.VertexCount, restored.VertexCount);
                Assert.AreEqual(source.TriangleCount, restored.TriangleCount);

                for (int i = 0; i < source.VertexCount; i++)
                {
                    Assert.Less(math.distance(source.Vertices[i], restored.Vertices[i]), 1e-4f);
                    Assert.Less(math.distance(source.Normals[i], restored.Normals[i]), 1e-4f);
                }

                for (int t = 0; t < source.TriangleCount; t++)
                    Assert.AreEqual(source.Triangles[t], restored.Triangles[t]);
            }
            finally
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
                if (restored.Vertices.IsCreated) restored.Dispose();
                source.Dispose();
            }
        }

        [Test]
        public void ToRenderMesh_LargeVertexCount_UsesUInt32Indices()
        {
            // 256x256 cells → 257*257 = 66049 verts > 65535 → forces 32-bit index buffer.
            var data = TestMeshFactory.BuildPlane(256, 256f, Allocator.Persistent);
            Mesh mesh = null;
            try
            {
                Assert.Greater(data.VertexCount, ushort.MaxValue);
                mesh = MeshDataConversions.ToRenderMesh(data);

                Assert.AreEqual(UnityEngine.Rendering.IndexFormat.UInt32, mesh.indexFormat);
                Assert.AreEqual(data.VertexCount, mesh.vertexCount);
            }
            finally
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
                data.Dispose();
            }
        }
    }
}
