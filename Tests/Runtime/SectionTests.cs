using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain.Tests
{
    public class SectionTests
    {
        [Test]
        public void CompileSection_CreatesLODRenderersAndCollider()
        {
            using var source = TestMeshFactory.BuildPlane(8, 40f, Allocator.TempJob);
            var grid = new GridSettings { CellSize = 20f, Is2D = true };
            var partition = MeshPartitioner.Partition(source, grid, allocator: Allocator.TempJob);
            CompiledSection compiled = null;
            try
            {
                var settings = TestSettings(generateLods: true, skirts: false, collision: true);
                compiled = SectionCompiler.CompileSection(
                    partition.Sections[0],
                    null,
                    partition.Dims,
                    partition.SectionCoords[0],
                    settings);

                Assert.AreEqual(partition.SectionCoords[0], compiled.Coord);
                Assert.IsNotNull(compiled.Root.GetComponent<LODGroup>());
                Assert.AreEqual(3, compiled.RenderMeshes.Length);
                Assert.AreEqual(3, compiled.Root.GetComponentsInChildren<MeshRenderer>().Length);

                var collider = compiled.Root.GetComponent<MeshCollider>();
                Assert.IsNotNull(collider);
                Assert.AreSame(compiled.CollisionMesh, collider.sharedMesh);
            }
            finally
            {
                compiled?.Dispose();
                partition.Dispose();
            }
        }

        [Test]
        public void CompileSection_UsesGridCellOriginAsRootAndOffsetsMeshVertices()
        {
            using var source = TestMeshFactory.BuildPlane(2, 20f, Allocator.TempJob);
            var grid = new GridSettings { CellSize = 10f, Is2D = true };
            var dims = GridDimensions.ComputeGridDimensions(new float3(0, 0, 0), new float3(20, 0, 20), grid);
            int3 coord = dims.AbsoluteCoord(new int3(1, 0, 1));
            CompiledSection compiled = null;
            try
            {
                compiled = SectionCompiler.CompileSection(source, null, dims, coord,
                    TestSettings(generateLods: false, skirts: false, collision: false));

                float3 expectedOrigin = dims.CellMin(new int3(1, 0, 1));
                Assert.AreEqual((Vector3)expectedOrigin, compiled.Root.transform.localPosition);

                Vector3[] vertices = compiled.RenderMeshes[0].vertices;
                Assert.Less(math.distance((float3)vertices[0], source.Vertices[0] - expectedOrigin), 1e-4f);
            }
            finally { compiled?.Dispose(); }
        }

        [Test]
        public void Skirt_AddsBoundaryBandAndCopiesAttributesAndWeights()
        {
            using var source = TestMeshFactory.BuildPlane(2, 20f, Allocator.TempJob);
            using var weights = new WeightLayerSet(Allocator.TempJob);
            var grass = weights.InitializeLayer("Grass", source.VertexCount);
            for (int i = 0; i < source.VertexCount; i++)
                grass[i] = source.Vertices[i].x;

            var grid = new GridSettings { CellSize = 100f, Is2D = true };
            var dims = GridDimensions.ComputeGridDimensions(new float3(0, 0, 0), new float3(20, 0, 20), grid);
            CompiledSection compiled = null;
            try
            {
                var settings = TestSettings(generateLods: false, skirts: true, collision: false);
                settings.Skirt.Width = 1f;
                settings.Skirt.PushDown = 3f;

                compiled = SectionCompiler.CompileSection(source, weights, dims, dims.OriginCoord, settings);

                Mesh mesh = compiled.RenderMeshes[0];
                Assert.Greater(mesh.vertexCount, source.VertexCount);
                Assert.Greater(mesh.triangles.Length / 3, source.TriangleCount);
                Assert.Less(mesh.bounds.min.y, -2.9f, "Skirt vertices should be pushed below the source plane.");
                Assert.AreEqual(mesh.vertexCount, mesh.normals.Length);
                Assert.AreEqual(mesh.vertexCount, mesh.uv.Length);

                var packedWeights = new List<Vector4>();
                mesh.GetUVs(2, packedWeights);
                Assert.AreEqual(mesh.vertexCount, packedWeights.Count);
                Assert.AreEqual(grass[0], packedWeights[0].x, 1e-4f);
            }
            finally { compiled?.Dispose(); }
        }

        [Test]
        public void LODs_DoNotIncreaseTriangleCountsAndPreservePackedWeightUVs()
        {
            using var source = TestMeshFactory.BuildPlane(16, 64f, Allocator.TempJob);
            using var weights = new WeightLayerSet(Allocator.TempJob);
            var grass = weights.InitializeLayer("Grass", source.VertexCount);
            for (int i = 0; i < source.VertexCount; i++)
                grass[i] = i / (float)source.VertexCount;

            var grid = new GridSettings { CellSize = 100f, Is2D = true };
            var dims = GridDimensions.ComputeGridDimensions(new float3(0, 0, 0), new float3(64, 0, 64), grid);
            CompiledSection compiled = null;
            try
            {
                compiled = SectionCompiler.CompileSection(source, weights, dims, dims.OriginCoord,
                    TestSettings(generateLods: true, skirts: false, collision: false));

                int previousTriangles = int.MaxValue;
                foreach (var mesh in compiled.RenderMeshes)
                {
                    int triCount = mesh.triangles.Length / 3;
                    Assert.LessOrEqual(triCount, previousTriangles);
                    previousTriangles = triCount;

                    var packedWeights = new List<Vector4>();
                    mesh.GetUVs(2, packedWeights);
                    Assert.AreEqual(mesh.vertexCount, packedWeights.Count);
                }
            }
            finally { compiled?.Dispose(); }
        }

        [Test]
        public void Collision_UsesOriginalUnskirtedSectionMesh()
        {
            using var source = TestMeshFactory.BuildPlane(4, 40f, Allocator.TempJob);
            var grid = new GridSettings { CellSize = 100f, Is2D = true };
            var dims = GridDimensions.ComputeGridDimensions(new float3(0, 0, 0), new float3(40, 0, 40), grid);
            CompiledSection compiled = null;
            try
            {
                compiled = SectionCompiler.CompileSection(source, null, dims, dims.OriginCoord,
                    TestSettings(generateLods: false, skirts: true, collision: true));

                Assert.Greater(compiled.RenderMeshes[0].triangles.Length / 3, source.TriangleCount);
                Assert.AreEqual(source.TriangleCount, compiled.CollisionMesh.triangles.Length / 3);
            }
            finally { compiled?.Dispose(); }
        }

        static SectionCompilationSettings TestSettings(bool generateLods, bool skirts, bool collision)
            => new SectionCompilationSettings
            {
                GenerateLODs = generateLods,
                GenerateCollision = collision,
                LODQualities = generateLods ? new[] { 1f, 0.5f, 0.25f } : new[] { 1f },
                LODScreenRelativeTransitionHeights = generateLods ? new[] { 0.6f, 0.3f, 0.1f } : new[] { 0.6f },
                Skirt = new MeshSkirtSettings
                {
                    Enabled = skirts,
                    Width = 1f,
                    PushDown = 2f,
                    PushMethod = MeshSkirtPushMethod.FixedDown,
                    VertexSnapTolerance = 1e-4f,
                    BoundaryMinPerimeter = 0f,
                },
            };
    }
}
