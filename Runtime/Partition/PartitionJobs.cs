using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Burst jobs implementing the spatial partition (Phase 1). Bucket-sort approach from
    /// <c>doc/06_BURST_AND_COMPUTE.md §2</c>: assign each triangle to the cell containing its centroid
    /// (no clipping), prefix-sum + scatter into a flat per-cell triangle list, then build one
    /// <see cref="MeshData"/> per non-empty cell. Replaces UE's atomic compare-and-swap tie-break — the
    /// centroid falls in exactly one cell, so the result is deterministic without atomics.
    /// </summary>

    /// <summary>
    /// Assigns each triangle to the linear index of the cell containing its centroid.
    /// Port of <c>06 §2.1</c> (axis-adjusted: 2D collapses Y).
    /// </summary>
    [BurstCompile]
    public struct AssignTrianglesToCellsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> Vertices;
        [ReadOnly] public NativeArray<int3> Triangles;

        public float3 SnappedMin;
        public float CellSize;
        public int3 CellNumber;
        public bool Is2D;

        [WriteOnly] public NativeArray<int> TriangleCell;

        public void Execute(int triIndex)
        {
            int3 t = Triangles[triIndex];
            float3 centroid = (Vertices[t.x] + Vertices[t.y] + Vertices[t.z]) / 3f;

            int3 coord = (int3)math.floor((centroid - SnappedMin) / CellSize);
            coord = math.clamp(coord, int3.zero, CellNumber - 1);
            if (Is2D) coord.y = 0;

            // Linear index must match GridDimensions.LinearIndex: x + z*nx + y*nx*nz.
            TriangleCell[triIndex] = coord.x
                                   + coord.z * CellNumber.x
                                   + coord.y * CellNumber.x * CellNumber.z;
        }
    }

    /// <summary>
    /// Counts triangles per cell and scatters triangle indices into a flat per-cell list.
    /// Single-threaded (prefix-sum + scatter is inherently sequential); cheap relative to mesh build.
    /// </summary>
    [BurstCompile]
    public struct BucketTrianglesJob : IJob
    {
        [ReadOnly] public NativeArray<int> TriangleCell;

        /// <summary>Per-cell triangle count. Length = TotalCells. Must be zero-initialised.</summary>
        public NativeArray<int> CellTriCounts;

        /// <summary>Per-cell start offset into <see cref="SortedTriangles"/>. Length = TotalCells.</summary>
        public NativeArray<int> CellTriStart;

        /// <summary>Triangle indices grouped by cell. Length = triangle count.</summary>
        public NativeArray<int> SortedTriangles;

        public void Execute()
        {
            int triCount = TriangleCell.Length;

            // Count.
            for (int t = 0; t < triCount; t++)
                CellTriCounts[TriangleCell[t]]++;

            // Exclusive prefix-sum → start offsets.
            int running = 0;
            for (int c = 0; c < CellTriCounts.Length; c++)
            {
                CellTriStart[c] = running;
                running += CellTriCounts[c];
            }

            // Scatter using a moving cursor per cell.
            var cursor = new NativeArray<int>(CellTriStart.Length, Allocator.Temp);
            for (int c = 0; c < CellTriStart.Length; c++)
                cursor[c] = CellTriStart[c];

            for (int t = 0; t < triCount; t++)
            {
                int cell = TriangleCell[t];
                SortedTriangles[cursor[cell]++] = t;
            }

            cursor.Dispose();
        }
    }

    /// <summary>
    /// Counts the unique vertices of each non-empty section, so the orchestrator can allocate each
    /// section's <see cref="MeshData"/> exactly once (the fixed-size format has no append path).
    /// One section = one parallel index.
    /// </summary>
    [BurstCompile]
    public struct CountSectionVerticesJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int3> Triangles;
        [ReadOnly] public NativeArray<int> SortedTriangles;

        /// <summary>For each section: (start offset, triangle count) into <see cref="SortedTriangles"/>.</summary>
        [ReadOnly] public NativeArray<int2> SectionRanges;

        [WriteOnly] public NativeArray<int> SectionVertexCounts;

        public void Execute(int sectionIndex)
        {
            int2 range = SectionRanges[sectionIndex];
            int start = range.x;
            int count = range.y;

            var seen = new NativeHashSet<int>(count * 3, Allocator.Temp);
            for (int i = 0; i < count; i++)
            {
                int3 tri = Triangles[SortedTriangles[start + i]];
                seen.Add(tri.x);
                seen.Add(tri.y);
                seen.Add(tri.z);
            }

            SectionVertexCounts[sectionIndex] = seen.Count;
            seen.Dispose();
        }
    }

    /// <summary>
    /// Builds the geometry + per-vertex/per-triangle attributes of <b>one</b> section into its
    /// pre-allocated buffers. Remaps source vertex ids to compact section-local ids and transfers any
    /// attribute buffers the source carries. Scheduled once per non-empty section (as an <see cref="IJob"/>)
    /// so the job captures that section's buffers directly — this avoids nested native containers
    /// (a <c>NativeArray&lt;MeshData&gt;</c> would be rejected by Burst's safety system) while still
    /// running all sections in parallel via combined <see cref="JobHandle"/>s.
    ///
    /// Weight layers travel separately (managed side-car) since Burst cannot hold the name→layer map.
    /// <see cref="VertexMapSource"/> exposes the section-local→source vertex mapping so the orchestrator
    /// can copy weight-layer values using the same remap.
    /// </summary>
    [BurstCompile]
    public struct BuildSectionMeshJob : IJob
    {
        // Source (read-only). The same source buffers are shared by every section job; reads only.
        [ReadOnly] public NativeArray<float3> SrcVertices;
        [ReadOnly] public NativeArray<int3> SrcTriangles;
        [ReadOnly] public NativeArray<float3> SrcNormals;
        [ReadOnly] public NativeArray<float2> SrcChannelUVs;
        [ReadOnly] public NativeArray<float2> SrcSourceUV0;
        [ReadOnly] public NativeArray<int> SrcBaseIDs;

        public bool HasNormals;
        public bool HasChannelUVs;
        public bool HasSourceUV0;
        public bool HasBaseIDs;

        /// <summary>This section's slice of the bucketed triangle list (start, count).</summary>
        [ReadOnly] public NativeArray<int> SortedTriangles;
        public int TriStart;
        public int TriCount;

        // This section's pre-allocated output buffers (the section MeshData's NativeArrays).
        public NativeArray<float3> DstVertices;
        public NativeArray<int3> DstTriangles;
        public NativeArray<float3> DstNormals;
        public NativeArray<float2> DstChannelUVs;
        public NativeArray<float2> DstSourceUV0;
        public NativeArray<int> DstBaseIDs;

        /// <summary>
        /// section-local vid → source vid, written for the orchestrator's weight-layer transfer.
        /// Length = section vertex count.
        /// </summary>
        [WriteOnly] public NativeArray<int> VertexMapSource;

        public void Execute()
        {
            var remap = new NativeHashMap<int, int>(TriCount * 3, Allocator.Temp);
            int nextVid = 0;

            for (int i = 0; i < TriCount; i++)
            {
                int srcTri = SortedTriangles[TriStart + i];
                int3 tri = SrcTriangles[srcTri];

                int a = MapVertex(tri.x, remap, ref nextVid);
                int b = MapVertex(tri.y, remap, ref nextVid);
                int c = MapVertex(tri.z, remap, ref nextVid);

                DstTriangles[i] = new int3(a, b, c);
                if (HasBaseIDs) DstBaseIDs[i] = SrcBaseIDs[srcTri];
            }

            remap.Dispose();
        }

        int MapVertex(int srcVid, NativeHashMap<int, int> remap, ref int nextVid)
        {
            if (remap.TryGetValue(srcVid, out int existing))
                return existing;

            int localVid = nextVid++;
            remap.Add(srcVid, localVid);

            DstVertices[localVid] = SrcVertices[srcVid];
            if (HasNormals) DstNormals[localVid] = SrcNormals[srcVid];
            if (HasChannelUVs) DstChannelUVs[localVid] = SrcChannelUVs[srcVid];
            if (HasSourceUV0) DstSourceUV0[localVid] = SrcSourceUV0[srcVid];

            VertexMapSource[localVid] = srcVid;

            return localVid;
        }
    }
}
