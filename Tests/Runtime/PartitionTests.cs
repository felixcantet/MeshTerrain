using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Fca.MeshTerrain.Tests
{
    /// <summary>
    /// EditMode tests for the Phase 1 spatial partition (grid bucket-sort). Inputs come from
    /// <see cref="TestMeshFactory.BuildPlane"/> (flat on XZ, +Y up), matching the Unity-native axis
    /// convention where <c>Is2D</c> collapses the Y column.
    /// </summary>
    public class PartitionTests
    {
        // ---- ComputeGridDimensions ----

        [Test]
        public void ComputeGridDimensions_Is2D_CollapsesYAndSnapsOrigin()
        {
            var grid = new GridSettings { CellSize = 10f, Is2D = true, WorldOriginOffset = float3.zero };
            // A box from (5,0,5) to (25,3,25): origin floor-snaps down to (0,*,0); extent 25 → 3 cells.
            var dims = GridDimensions.ComputeGridDimensions(new float3(5, 0, 5), new float3(25, 3, 25), grid);

            Assert.AreEqual(new int3(0, 0, 0), dims.OriginCoord);
            Assert.AreEqual(new float3(0, 0, 0), dims.SnappedMin);
            Assert.AreEqual(1, dims.CellNumber.y, "Is2D must collapse Y to a single cell.");
            Assert.AreEqual(3, dims.CellNumber.x);
            Assert.AreEqual(3, dims.CellNumber.z);
            // 2D cell spans the full Y extent of the mesh.
            Assert.AreEqual(3f, dims.CellExtent.y, 1e-4f);
        }

        [Test]
        public void ComputeGridDimensions_NonZeroAnchor_ShiftsOriginCoord()
        {
            var grid = new GridSettings { CellSize = 10f, Is2D = true, WorldOriginOffset = new float3(100, 0, 100) };
            var dims = GridDimensions.ComputeGridDimensions(new float3(105, 0, 105), new float3(125, 1, 125), grid);

            // (105-100)/10 = 0.5 → floor 0; snapped min = 0*10 + 100 = 100.
            Assert.AreEqual(new int3(0, 0, 0), dims.OriginCoord);
            Assert.AreEqual(new float3(100, 0, 100), dims.SnappedMin);
        }

        [Test]
        public void ComputeGridDimensions_AbsoluteCoords_IndependentOfMeshBounds()
        {
            // The cache-stability guarantee: for a FIXED anchor, the absolute coordinate of a given world
            // point does not depend on the mesh bounds that happened to be passed in. So when geometry is
            // edited and the bounds grow/shrink, the cells covering unchanged regions keep their keys.
            // (Anchoring on the mesh bounds instead would break this — hence the anchor-shifted snap.)
            var grid = new GridSettings { CellSize = 10f, Is2D = true, WorldOriginOffset = float3.zero };

            // Same anchor, two different mesh extents that both contain the probe point.
            var dimsSmall = GridDimensions.ComputeGridDimensions(new float3(0, 0, 0), new float3(20, 1, 20), grid);
            var dimsLarge = GridDimensions.ComputeGridDimensions(new float3(-50, 0, -50), new float3(80, 1, 80), grid);

            float3 worldPoint = new float3(12.5f, 0f, 7.5f);
            int3 absSmall = AbsoluteCoordOf(dimsSmall, worldPoint, grid);
            int3 absLarge = AbsoluteCoordOf(dimsLarge, worldPoint, grid);

            Assert.AreEqual(absSmall, absLarge,
                "Absolute cell coordinate of a world point must not depend on the mesh bounds (cache stability).");
            // And it equals the direct anchor-relative coordinate floor((p - anchor)/cell).
            Assert.AreEqual(new int3(1, 0, 0), absSmall);
        }

        static int3 AbsoluteCoordOf(in GridDimensions dims, float3 worldPoint, in GridSettings grid)
        {
            int3 local = (int3)math.floor((worldPoint - dims.SnappedMin) / grid.CellSize);
            if (grid.Is2D) local.y = 0;
            return dims.AbsoluteCoord(local);
        }

        [Test]
        public void LinearIndex_RoundTripsWithLocalCoord()
        {
            var dims = new GridDimensions { CellNumber = new int3(3, 1, 4) };
            for (int i = 0; i < dims.TotalCells; i++)
                Assert.AreEqual(i, dims.LinearIndex(dims.LocalCoord(i)), "LinearIndex/LocalCoord must round-trip.");
        }

        // ---- Partition ----

        [Test]
        public void Partition_SingleCell_ReturnsWholeMesh()
        {
            using var source = TestMeshFactory.BuildPlane(cells: 4, size: 100f, Allocator.TempJob);
            // CellSize larger than the mesh → exactly one section.
            var grid = new GridSettings { CellSize = 1000f, Is2D = true };

            var result = MeshPartitioner.Partition(source, grid, allocator: Allocator.TempJob);
            try
            {
                Assert.AreEqual(1, result.SectionCount);
                Assert.AreEqual(source.TriangleCount, result.Sections[0].TriangleCount);
                Assert.AreEqual(source.VertexCount, result.Sections[0].VertexCount);
            }
            finally { result.Dispose(); }
        }

        [Test]
        public void Partition_Conserves_AllTrianglesExactlyOnce()
        {
            using var source = TestMeshFactory.BuildPlane(cells: 16, size: 160f, Allocator.TempJob);
            var grid = new GridSettings { CellSize = 40f, Is2D = true }; // 4x4 grid

            var result = MeshPartitioner.Partition(source, grid, allocator: Allocator.TempJob);
            try
            {
                int sumTris = 0;
                foreach (var sec in result.Sections) sumTris += sec.TriangleCount;
                Assert.AreEqual(source.TriangleCount, sumTris,
                    "Sum of section triangles must equal the source (no loss, no duplication).");

                // Every section triangle must land in the cell its centroid maps to.
                for (int si = 0; si < result.SectionCount; si++)
                {
                    var sec = result.Sections[si];
                    int3 expectedCoord = result.SectionCoords[si];
                    for (int t = 0; t < sec.TriangleCount; t++)
                    {
                        int3 tri = sec.Triangles[t];
                        float3 centroid = (sec.Vertices[tri.x] + sec.Vertices[tri.y] + sec.Vertices[tri.z]) / 3f;
                        int3 coord = (int3)math.floor((centroid - result.Dims.SnappedMin) / grid.CellSize);
                        coord.y = 0; // Is2D
                        coord = math.clamp(coord, int3.zero, result.Dims.CellNumber - 1);
                        Assert.AreEqual(expectedCoord, result.Dims.AbsoluteCoord(coord),
                            "A section triangle's centroid maps to a different cell than its section.");
                    }
                }
            }
            finally { result.Dispose(); }
        }

        [Test]
        public void Partition_RemappedTriangles_ReferenceOnlySectionVertices()
        {
            using var source = TestMeshFactory.BuildPlane(cells: 8, size: 80f, Allocator.TempJob);
            var grid = new GridSettings { CellSize = 20f, Is2D = true }; // 4x4

            var result = MeshPartitioner.Partition(source, grid, allocator: Allocator.TempJob);
            try
            {
                foreach (var sec in result.Sections)
                {
                    for (int t = 0; t < sec.TriangleCount; t++)
                    {
                        int3 tri = sec.Triangles[t];
                        Assert.IsTrue(tri.x >= 0 && tri.x < sec.VertexCount);
                        Assert.IsTrue(tri.y >= 0 && tri.y < sec.VertexCount);
                        Assert.IsTrue(tri.z >= 0 && tri.z < sec.VertexCount);
                    }
                }
            }
            finally { result.Dispose(); }
        }

        [Test]
        public void Partition_TransfersVertexAttributes()
        {
            // BuildPlane carries Normals, ChannelUVs and BaseIDs.
            using var source = TestMeshFactory.BuildPlane(cells: 6, size: 60f, Allocator.TempJob);
            var grid = new GridSettings { CellSize = 20f, Is2D = true }; // 3x3

            var result = MeshPartitioner.Partition(source, grid, allocator: Allocator.TempJob);
            try
            {
                // Build a position→source-index lookup to verify per-vertex attributes match.
                var srcByPos = new Dictionary<float3, int>();
                for (int i = 0; i < source.VertexCount; i++) srcByPos[source.Vertices[i]] = i;

                foreach (var sec in result.Sections)
                {
                    Assert.IsTrue(sec.HasNormals);
                    Assert.IsTrue(sec.HasChannelUVs);
                    Assert.IsTrue(sec.HasBaseIDs);

                    for (int v = 0; v < sec.VertexCount; v++)
                    {
                        int srcIdx = srcByPos[sec.Vertices[v]];
                        Assert.AreEqual(source.Normals[srcIdx], sec.Normals[v]);
                        Assert.AreEqual(source.ChannelUVs[srcIdx], sec.ChannelUVs[v]);
                    }
                }
            }
            finally { result.Dispose(); }
        }

        [Test]
        public void Partition_TransfersWeightLayers()
        {
            using var source = TestMeshFactory.BuildPlane(cells: 4, size: 40f, Allocator.TempJob);
            var grid = new GridSettings { CellSize = 20f, Is2D = true }; // 2x2

            using var weights = new WeightLayerSet(Allocator.TempJob);
            var grass = weights.InitializeLayer("Grass", source.VertexCount);
            for (int i = 0; i < source.VertexCount; i++)
                grass[i] = source.Vertices[i].x; // a known per-vertex signal

            var result = MeshPartitioner.Partition(source, grid, weights, Allocator.TempJob);
            try
            {
                Assert.IsNotNull(result.SectionWeights);
                for (int si = 0; si < result.SectionCount; si++)
                {
                    var sec = result.Sections[si];
                    var sw = result.SectionWeights[si];
                    Assert.IsTrue(sw.HasLayer("Grass"));
                    sw.TryGetLayer("Grass", out var secGrass);
                    for (int v = 0; v < sec.VertexCount; v++)
                        Assert.AreEqual(sec.Vertices[v].x, secGrass[v], 1e-4f,
                            "Weight-layer value must follow its vertex through the partition remap.");
                }
            }
            finally { result.Dispose(); }
        }

        [Test]
        public void Partition_LargeMesh_DoesNotThrow()
        {
            // ~500k triangles (cells^2 * 2). Perf sanity, not asserted tight.
            using var source = TestMeshFactory.BuildPlane(cells: 500, size: 5000f, Allocator.TempJob);
            var grid = new GridSettings { CellSize = 500f, Is2D = true }; // 10x10

            var result = MeshPartitioner.Partition(source, grid, allocator: Allocator.TempJob);
            try
            {
                int sumTris = 0;
                foreach (var sec in result.Sections) sumTris += sec.TriangleCount;
                Assert.AreEqual(source.TriangleCount, sumTris);
            }
            finally { result.Dispose(); }
        }
    }
}
