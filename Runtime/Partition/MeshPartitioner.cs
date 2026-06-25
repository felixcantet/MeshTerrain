using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Result of <see cref="MeshPartitioner.Partition"/>. Owns the produced section
    /// <see cref="MeshData"/>s and their weight-layer side-cars; the caller must
    /// <see cref="Dispose"/> it (which disposes every section). Section <c>i</c> in
    /// <see cref="Sections"/> lives in the cell whose absolute coordinate is
    /// <see cref="SectionCoords"/><c>[i]</c> — the stable key the incremental cache (Phase 5) will hash.
    /// </summary>
    public struct PartitionResult : IDisposable
    {
        public GridDimensions Dims;

        /// <summary>Absolute grid coordinate (the section key) of each section.</summary>
        public int3[] SectionCoords;

        /// <summary>One <see cref="MeshData"/> per non-empty cell.</summary>
        public MeshData[] Sections;

        /// <summary>Weight-layer side-car per section, or null when the source had no weight layers.</summary>
        public WeightLayerSet[] SectionWeights;

        public int SectionCount => Sections?.Length ?? 0;

        public void Dispose()
        {
            if (Sections != null)
                foreach (var s in Sections) s.Dispose();

            if (SectionWeights != null)
                foreach (var w in SectionWeights) w?.Dispose();

            Sections = null;
            SectionWeights = null;
            SectionCoords = null;
        }
    }

    /// <summary>
    /// Partitions one large <see cref="MeshData"/> into per-cell sections on a stable, anchor-aligned
    /// grid (Phase 1). Triangles are assigned to the cell containing their centroid (no clipping —
    /// jagged borders are expected and masked by skirts in Phase 3), bucket-sorted, and each non-empty
    /// cell is rebuilt into its own <see cref="MeshData"/> with attributes transferred. Orchestrates
    /// the Burst jobs in <c>PartitionJobs.cs</c> (see <c>doc/02_SYSTEM_ANALYSIS.md §4</c>,
    /// <c>doc/06_BURST_AND_COMPUTE.md §2 &amp; §5</c>).
    /// </summary>
    public static class MeshPartitioner
    {
        /// <summary>
        /// Partitions <paramref name="source"/> using <paramref name="grid"/>. The optional
        /// <paramref name="weights"/> side-car (per-vertex weight layers of the source) is split along
        /// with the geometry. Allocates the returned sections with <paramref name="allocator"/>; the
        /// caller owns and must dispose the <see cref="PartitionResult"/>.
        /// </summary>
        public static PartitionResult Partition(
            in MeshData source,
            in GridSettings grid,
            WeightLayerSet weights = null,
            Allocator allocator = Allocator.Persistent)
        {
            int triCount = source.TriangleCount;
            int vertCount = source.VertexCount;

            // --- Bounds (mesh-local AABB) ---
            ComputeBounds(source.Vertices, out float3 boundsMin, out float3 boundsMax);
            var dims = GridDimensions.ComputeGridDimensions(boundsMin, boundsMax, grid);
            int totalCells = dims.TotalCells;

            // --- Assign + bucket ---
            var triangleCell = new NativeArray<int>(triCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var cellCounts = new NativeArray<int>(totalCells, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var cellStart = new NativeArray<int>(totalCells, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var sortedTriangles = new NativeArray<int>(triCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            var assign = new AssignTrianglesToCellsJob
            {
                Vertices = source.Vertices,
                Triangles = source.Triangles,
                SnappedMin = dims.SnappedMin,
                CellSize = grid.CellSize,
                CellNumber = dims.CellNumber,
                Is2D = grid.Is2D,
                TriangleCell = triangleCell,
            };
            JobHandle assignHandle = assign.Schedule(triCount, 64);

            var bucket = new BucketTrianglesJob
            {
                TriangleCell = triangleCell,
                CellTriCounts = cellCounts,
                CellTriStart = cellStart,
                SortedTriangles = sortedTriangles,
            };
            bucket.Schedule(assignHandle).Complete();

            triangleCell.Dispose();

            // --- Compact to non-empty cells ---
            int nonEmpty = 0;
            for (int c = 0; c < totalCells; c++)
                if (cellCounts[c] > 0) nonEmpty++;

            var sectionRanges = new NativeArray<int2>(nonEmpty, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var sectionCoords = new int3[nonEmpty];

            int s = 0;
            for (int c = 0; c < totalCells; c++)
            {
                if (cellCounts[c] == 0) continue;
                sectionRanges[s] = new int2(cellStart[c], cellCounts[c]);
                sectionCoords[s] = dims.AbsoluteCoord(dims.LocalCoord(c));
                s++;
            }

            // --- Count unique vertices per section (so each MeshData is allocated exactly once) ---
            var sectionVertexCounts = new NativeArray<int>(nonEmpty, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var countVerts = new CountSectionVerticesJob
            {
                Triangles = source.Triangles,
                SortedTriangles = sortedTriangles,
                SectionRanges = sectionRanges,
                SectionVertexCounts = sectionVertexCounts,
            };
            countVerts.Schedule(nonEmpty, 1).Complete();

            // --- Allocate sections + build (one IJob per section, all combined) ---
            var sections = new MeshData[nonEmpty];
            var vertexMaps = new NativeArray<int>[nonEmpty];   // section-local vid → source vid
            var handles = new NativeArray<JobHandle>(nonEmpty, Allocator.Temp);

            // Shared, read-only length-0 stand-ins for absent SOURCE attributes (Burst requires created
            // arrays even when the Has* flag gates access). Read-only sharing across parallel jobs is safe.
            var srcEmpty2 = new NativeArray<float2>(0, Allocator.TempJob);
            var srcEmpty3 = new NativeArray<float3>(0, Allocator.TempJob);
            var srcEmptyI = new NativeArray<int>(0, Allocator.TempJob);

            // Absent DST attributes need a distinct writable stand-in PER section — a single shared
            // writable array across parallel jobs would trip the job safety system's aliasing check.
            var dstEmpties = new System.Collections.Generic.List<IDisposable>();

            for (int i = 0; i < nonEmpty; i++)
            {
                int2 range = sectionRanges[i];
                int sVerts = sectionVertexCounts[i];
                int sTris = range.y;

                var section = MeshData.Allocate(
                    sVerts, sTris, allocator,
                    withNormals: source.HasNormals,
                    withChannelUVs: source.HasChannelUVs,
                    withSourceUV0: source.HasSourceUV0,
                    withBaseIDs: source.HasBaseIDs);
                sections[i] = section;

                var vertexMap = new NativeArray<int>(sVerts, allocator, NativeArrayOptions.UninitializedMemory);
                vertexMaps[i] = vertexMap;

                NativeArray<float3> dstNormals = section.Normals;
                NativeArray<float2> dstChannelUVs = section.ChannelUVs;
                NativeArray<float2> dstSourceUV0 = section.SourceUV0;
                NativeArray<int> dstBaseIDs = section.BaseIDLayer;
                if (!source.HasNormals) { dstNormals = new NativeArray<float3>(0, Allocator.TempJob); dstEmpties.Add(dstNormals); }
                if (!source.HasChannelUVs) { dstChannelUVs = new NativeArray<float2>(0, Allocator.TempJob); dstEmpties.Add(dstChannelUVs); }
                if (!source.HasSourceUV0) { dstSourceUV0 = new NativeArray<float2>(0, Allocator.TempJob); dstEmpties.Add(dstSourceUV0); }
                if (!source.HasBaseIDs) { dstBaseIDs = new NativeArray<int>(0, Allocator.TempJob); dstEmpties.Add(dstBaseIDs); }

                var build = new BuildSectionMeshJob
                {
                    SrcVertices = source.Vertices,
                    SrcTriangles = source.Triangles,
                    SrcNormals = source.HasNormals ? source.Normals : srcEmpty3,
                    SrcChannelUVs = source.HasChannelUVs ? source.ChannelUVs : srcEmpty2,
                    SrcSourceUV0 = source.HasSourceUV0 ? source.SourceUV0 : srcEmpty2,
                    SrcBaseIDs = source.HasBaseIDs ? source.BaseIDLayer : srcEmptyI,
                    HasNormals = source.HasNormals,
                    HasChannelUVs = source.HasChannelUVs,
                    HasSourceUV0 = source.HasSourceUV0,
                    HasBaseIDs = source.HasBaseIDs,
                    SortedTriangles = sortedTriangles,
                    TriStart = range.x,
                    TriCount = sTris,
                    DstVertices = section.Vertices,
                    DstTriangles = section.Triangles,
                    DstNormals = dstNormals,
                    DstChannelUVs = dstChannelUVs,
                    DstSourceUV0 = dstSourceUV0,
                    DstBaseIDs = dstBaseIDs,
                    VertexMapSource = vertexMap,
                };
                handles[i] = build.Schedule();
            }

            JobHandle.CombineDependencies(handles).Complete();
            handles.Dispose();
            foreach (var e in dstEmpties) e.Dispose();

            // --- Transfer weight layers via the managed side-car (Burst can't hold the name→layer map) ---
            WeightLayerSet[] sectionWeights = null;
            if (weights != null && weights.LayerCount > 0)
            {
                sectionWeights = new WeightLayerSet[nonEmpty];
                for (int i = 0; i < nonEmpty; i++)
                    sectionWeights[i] = TransferWeights(weights, vertexMaps[i], sections[i].VertexCount, allocator);
            }

            // --- Cleanup scratch ---
            for (int i = 0; i < nonEmpty; i++) vertexMaps[i].Dispose();
            srcEmpty2.Dispose(); srcEmpty3.Dispose(); srcEmptyI.Dispose();
            sortedTriangles.Dispose();
            cellCounts.Dispose();
            cellStart.Dispose();
            sectionRanges.Dispose();
            sectionVertexCounts.Dispose();

            return new PartitionResult
            {
                Dims = dims,
                SectionCoords = sectionCoords,
                Sections = sections,
                SectionWeights = sectionWeights,
            };
        }

        static WeightLayerSet TransferWeights(WeightLayerSet source, NativeArray<int> localToSource, int sectionVertCount, Allocator allocator)
        {
            var result = new WeightLayerSet(allocator);
            var names = source.LayerNames;
            for (int n = 0; n < names.Count; n++)
            {
                string name = names[n];
                if (!source.TryGetLayer(name, out var srcLayer)) continue;

                var dstLayer = result.InitializeLayer(name, sectionVertCount);
                for (int localVid = 0; localVid < sectionVertCount; localVid++)
                    dstLayer[localVid] = srcLayer[localToSource[localVid]];
            }
            return result;
        }

        /// <summary>Computes the AABB of a vertex buffer via a small Burst reduce.</summary>
        static void ComputeBounds(NativeArray<float3> vertices, out float3 min, out float3 max)
        {
            var result = new NativeArray<float3>(2, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            new BoundsJob { Vertices = vertices, MinMax = result }.Schedule().Complete();
            min = result[0];
            max = result[1];
            result.Dispose();
        }

        [BurstCompile]
        struct BoundsJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Vertices;
            [WriteOnly] public NativeArray<float3> MinMax;

            public void Execute()
            {
                float3 min = new float3(float.MaxValue);
                float3 max = new float3(float.MinValue);
                for (int i = 0; i < Vertices.Length; i++)
                {
                    min = math.min(min, Vertices[i]);
                    max = math.max(max, Vertices[i]);
                }
                MinMax[0] = min;
                MinMax[1] = max;
            }
        }
    }
}
