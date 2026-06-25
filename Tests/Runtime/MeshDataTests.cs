using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Fca.MeshTerrain.Tests
{
    public class MeshDataTests
    {
        [Test]
        public void Allocate_SetsCountsAndOptionalBuffers()
        {
            var data = MeshData.Allocate(4, 2, Allocator.Temp,
                withNormals: true, withChannelUVs: true, withSourceUV0: false, withBaseIDs: true);
            try
            {
                Assert.AreEqual(4, data.VertexCount);
                Assert.AreEqual(2, data.TriangleCount);
                Assert.IsTrue(data.HasNormals);
                Assert.IsTrue(data.HasChannelUVs);
                Assert.IsFalse(data.HasSourceUV0);
                Assert.IsTrue(data.HasBaseIDs);
            }
            finally
            {
                data.Dispose();
            }
        }

        [Test]
        public void ConstructQuad_InCode_HoldsExpectedData()
        {
            var data = MeshData.Allocate(4, 2, Allocator.Temp, withNormals: false, withBaseIDs: false);
            try
            {
                data.Vertices[0] = new float3(0, 0, 0);
                data.Vertices[1] = new float3(1, 0, 0);
                data.Vertices[2] = new float3(0, 0, 1);
                data.Vertices[3] = new float3(1, 0, 1);
                data.Triangles[0] = new int3(0, 2, 1);
                data.Triangles[1] = new int3(1, 2, 3);

                Assert.AreEqual(new float3(1, 0, 1), data.Vertices[3]);
                Assert.AreEqual(new int3(1, 2, 3), data.Triangles[1]);
                Assert.IsFalse(data.HasNormals);
            }
            finally
            {
                data.Dispose();
            }
        }

        [Test]
        public void Dispose_OnDefaultStruct_DoesNotThrow()
        {
            var data = default(MeshData);
            Assert.DoesNotThrow(() => data.Dispose());
        }

        [Test]
        public void WeightLayerSet_InitializeLayer_IsZeroFilledAndTracked()
        {
            using var layers = new WeightLayerSet(Allocator.Temp);
            var grass = layers.InitializeLayer("Grass", 5);

            Assert.AreEqual(5, grass.Length);
            for (int i = 0; i < grass.Length; i++)
                Assert.AreEqual(0f, grass[i]);

            Assert.IsTrue(layers.HasLayer("Grass"));
            Assert.AreEqual(0, layers.FindLayerIndex("Grass"));
            Assert.AreEqual(1, layers.LayerCount);
            CollectionAssert.Contains(layers.LayerNames, "Grass");
        }

        [Test]
        public void WeightLayerSet_SetGetValue_RoundTrips()
        {
            using var layers = new WeightLayerSet(Allocator.Temp);
            layers.InitializeLayer("Rock", 3);

            layers.SetValue("Rock", 2, 0.75f);

            Assert.AreEqual(0.75f, layers.GetValue("Rock", 2));
            Assert.AreEqual(0.75f, layers.GetLayerByIndex(0)[2]);
        }

        [Test]
        public void WeightLayerSet_InitializeExistingLayer_ReturnsSameInstance()
        {
            using var layers = new WeightLayerSet(Allocator.Temp);
            var first = layers.InitializeLayer("Sand", 4);
            var second = layers.InitializeLayer("Sand", 4);

            Assert.AreEqual(1, layers.LayerCount);
            Assert.IsTrue(first.Equals(second));
        }
    }
}
