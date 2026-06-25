using Unity.Collections;
using Unity.Mathematics;

namespace Fca.MeshTerrain.Tests
{
    /// <summary>Helpers to build deterministic <see cref="MeshData"/> inputs for tests.</summary>
    static class TestMeshFactory
    {
        /// <summary>
        /// Builds a flat grid on the XZ plane spanning [0,size] with <paramref name="cells"/> cells
        /// per axis (two triangles per cell). Normals point +Y; channel UVs map [0,1]² over the plane.
        /// </summary>
        public static MeshData BuildPlane(int cells, float size, Allocator allocator)
        {
            int verts = (cells + 1) * (cells + 1);
            int tris = cells * cells * 2;

            var data = MeshData.Allocate(verts, tris, allocator,
                withNormals: true, withChannelUVs: true, withSourceUV0: false, withBaseIDs: true);

            float step = size / cells;
            for (int z = 0; z <= cells; z++)
            {
                for (int x = 0; x <= cells; x++)
                {
                    int i = z * (cells + 1) + x;
                    data.Vertices[i] = new float3(x * step, 0f, z * step);
                    data.Normals[i] = new float3(0f, 1f, 0f);
                    data.ChannelUVs[i] = new float2((float)x / cells, (float)z / cells);
                }
            }

            int t = 0;
            for (int z = 0; z < cells; z++)
            {
                for (int x = 0; x < cells; x++)
                {
                    int v0 = z * (cells + 1) + x;
                    int v1 = v0 + 1;
                    int v2 = v0 + (cells + 1);
                    int v3 = v2 + 1;

                    data.Triangles[t] = new int3(v0, v2, v1);
                    data.BaseIDLayer[t] = 0;
                    t++;
                    data.Triangles[t] = new int3(v1, v2, v3);
                    data.BaseIDLayer[t] = 0;
                    t++;
                }
            }

            return data;
        }
    }
}
