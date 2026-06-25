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
        {
            int nx = Resolution.x, nz = Resolution.y;
            int vertsX = nx + 1, vertsZ = nz + 1;
            int vertexCount = vertsX * vertsZ;
            int triangleCount = nx * nz * 2;

            var mesh = MeshData.Allocate(vertexCount, triangleCount, allocator,
                withNormals: true, withChannelUVs: false, withSourceUV0: true, withBaseIDs: true);

            float2 half = Size * 0.5f;

            for (int z = 0; z < vertsZ; z++)
            {
                float tz = (float)z / nz;
                for (int x = 0; x < vertsX; x++)
                {
                    float tx = (float)x / nx;
                    int i = z * vertsX + x;

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
            for (int z = 0; z < nz; z++)
            {
                for (int x = 0; x < nx; x++)
                {
                    int v00 = z * vertsX + x;
                    int v10 = v00 + 1;
                    int v01 = v00 + vertsX;
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
