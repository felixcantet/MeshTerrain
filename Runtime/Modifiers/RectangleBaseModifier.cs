using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Base modifier that produces a regular rectangular grid mesh — the geometry the rest of the stack
    /// transforms. Unity port of UE <c>FRectangleGeneratorUtils::GenerateSectionMesh</c>
    /// (<c>MeshPartitionRectangleGenerator.cpp</c>). Lies flat on the XZ plane, +Y up (Unity-native).
    ///
    /// Vertex/triangle counts follow directly from <see cref="Resolution"/>, so the fixed-size
    /// <see cref="MeshData"/> is allocated exactly — no append/builder path is needed (which is why the
    /// builder deferred from Phase 0/1 isn't required this pass). An optional <see cref="HeightFn"/> hook
    /// supplies per-vertex height; this is the seam where a HeightmapImporter plugs in later (flat by default).
    /// </summary>
    public sealed class RectangleBaseModifier : ModifierComponent
    {
        /// <summary>Quad resolution along X and Z. Produces (x+1)*(z+1) vertices, x*z*2 triangles.</summary>
        public int2 Resolution = new int2(8, 8);

        /// <summary>World-space size of the rectangle on the XZ plane.</summary>
        public float2 Size = new float2(100f, 100f);

        /// <summary>Center of the rectangle in mesh-local space (XZ); Y is the base plane height.</summary>
        public float3 Center = float3.zero;

        /// <summary>Optional height function over UV [0,1]² → local Y offset. Null = flat.</summary>
        public Func<float2, float> HeightFn;

        public override bool IsBase => true;

        public override double GetComplexity() => (Resolution.x + 1) * (Resolution.y + 1);

        public override Bounds ComputeBounds()
        {
            float maxH = 0f;
            // ComputeBounds stays cheap; if a HeightFn is present, callers can widen Y themselves.
            var b = new Bounds(new Vector3(Center.x, Center.y, Center.z),
                new Vector3(Size.x, math.max(0.01f, maxH), Size.y));
            return b;
        }

        public override MeshData ProduceBaseMesh(Allocator allocator)
            => ProduceGrid(0, Resolution.x, 0, Resolution.y, allocator);

        /// <summary>
        /// Produces only the part of the grid whose quads intersect <paramref name="cellBounds"/> (mesh-local
        /// AABB). Used by the Phase 5 bounded per-cell build (<see cref="ModifierGroup.ProcessCell"/>): it
        /// emits a contiguous sub-block of the <b>same global grid</b> as <see cref="ProduceBaseMesh(Allocator)"/>,
        /// so each emitted vertex keeps the identical position/UV/<see cref="HeightFn"/> value it would have in a
        /// full build. The partitioner's centroid assignment then carves out exactly the requested cell
        /// (<c>doc/08_STREAMING_SYSTEM_DESIGN.md §6.2–6.3</c>). Quads spanning the bound edge are included whole
        /// so the cell's triangles (assigned by centroid) are never missing.
        /// </summary>
        public MeshData ProduceBaseMesh(Bounds cellBounds, Allocator allocator)
        {
            int nx = Resolution.x, nz = Resolution.y;
            float2 half = Size * 0.5f;
            float minX = Center.x - half.x, minZ = Center.z - half.y;

            // Map the bounds onto quad-column/row indices. A quad q spans world [min + q*step, min + (q+1)*step];
            // include every quad whose span intersects the bounds (inclusive), so border quads are kept whole.
            float stepX = Size.x / nx, stepZ = Size.y / nz;
            int qx0 = (int)math.floor((cellBounds.min.x - minX) / stepX);
            int qx1 = (int)math.floor((cellBounds.max.x - minX) / stepX);
            int qz0 = (int)math.floor((cellBounds.min.z - minZ) / stepZ);
            int qz1 = (int)math.floor((cellBounds.max.z - minZ) / stepZ);

            qx0 = math.clamp(qx0, 0, nx - 1);
            qx1 = math.clamp(qx1, 0, nx - 1);
            qz0 = math.clamp(qz0, 0, nz - 1);
            qz1 = math.clamp(qz1, 0, nz - 1);

            // No quad of this base lies in the bounds (bounds entirely off the rectangle): empty mesh.
            if (cellBounds.max.x < minX || cellBounds.min.x > minX + Size.x ||
                cellBounds.max.z < minZ || cellBounds.min.z > minZ + Size.y)
                return MeshData.Allocate(0, 0, allocator,
                    withNormals: true, withChannelUVs: false, withSourceUV0: true, withBaseIDs: true);

            return ProduceGrid(qx0, qx1 + 1, qz0, qz1 + 1, allocator);
        }

        /// <summary>
        /// Builds the vertices/quads of the global grid for column range [qxStart, qxEnd) and row range
        /// [qzStart, qzEnd) (quad indices). Vertex global indices (x,z) drive the position/UV/HeightFn so the
        /// sub-block is bit-identical to the matching part of the full grid.
        /// </summary>
        MeshData ProduceGrid(int qxStart, int qxEnd, int qzStart, int qzEnd, Allocator allocator)
        {
            int nx = Resolution.x, nz = Resolution.y;
            int colVerts = (qxEnd - qxStart) + 1;   // vertex columns spanning the quad range
            int rowVerts = (qzEnd - qzStart) + 1;
            int vertexCount = colVerts * rowVerts;
            int triangleCount = (qxEnd - qxStart) * (qzEnd - qzStart) * 2;

            var mesh = MeshData.Allocate(vertexCount, triangleCount, allocator,
                withNormals: true, withChannelUVs: false, withSourceUV0: true, withBaseIDs: true);

            float2 half = Size * 0.5f;

            for (int lz = 0; lz < rowVerts; lz++)
            {
                int z = qzStart + lz;               // global vertex row index
                float tz = (float)z / nz;
                for (int lx = 0; lx < colVerts; lx++)
                {
                    int x = qxStart + lx;           // global vertex column index
                    float tx = (float)x / nx;
                    int i = lz * colVerts + lx;

                    float2 uv = new float2(tx, tz);
                    float worldX = Center.x - half.x + tx * Size.x;
                    float worldZ = Center.z - half.y + tz * Size.y;
                    float height = HeightFn != null ? HeightFn(uv) : 0f;

                    mesh.Vertices[i] = new float3(worldX, Center.y + height, worldZ);
                    mesh.Normals[i] = new float3(0f, 1f, 0f);
                    mesh.SourceUV0[i] = uv;
                }
            }

            int t = 0;
            for (int lz = 0; lz < rowVerts - 1; lz++)
            {
                for (int lx = 0; lx < colVerts - 1; lx++)
                {
                    int v00 = lz * colVerts + lx;
                    int v10 = v00 + 1;
                    int v01 = v00 + colVerts;
                    int v11 = v01 + 1;

                    // CCW winding with +Y up (matches the Phase 0 TestMeshFactory plane).
                    mesh.Triangles[t] = new int3(v00, v01, v10);
                    mesh.BaseIDLayer[t] = 0;
                    t++;
                    mesh.Triangles[t] = new int3(v10, v01, v11);
                    mesh.BaseIDLayer[t] = 0;
                    t++;
                }
            }

            return mesh;
        }
    }
}
