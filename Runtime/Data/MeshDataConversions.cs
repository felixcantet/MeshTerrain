using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Conversions between the <see cref="MeshData"/> pivot format and <see cref="UnityEngine.Mesh"/>.
    /// Uses the zero-copy <c>Mesh.MeshData</c> API where possible (see
    /// <c>doc/06_BURST_AND_COMPUTE.md §4</c>). Equivalent of UE <c>FMeshData</c> conversions
    /// (<c>ConvertToMeshDescription</c> / <c>ConvertToTriMeshCollisionData</c>).
    /// </summary>
    public static class MeshDataConversions
    {
        /// <summary>
        /// Builds a render-ready <see cref="UnityEngine.Mesh"/>: positions, normals, the channel
        /// (atlas) UV in UV0, and the source UV in UV1 when present. Uses a 32-bit index buffer
        /// automatically when the vertex count exceeds 65535.
        /// </summary>
        public static Mesh ToRenderMesh(in MeshData source, string name = null)
        {
            int vertexCount = source.VertexCount;
            int triangleCount = source.TriangleCount;
            int indexCount = triangleCount * 3;

            var indexFormat = vertexCount > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;

            var meshDataArray = Mesh.AllocateWritableMeshData(1);
            Mesh.MeshData writable = meshDataArray[0];

            // The render layout always carries a Normal channel (so RecalculateNormals can run when
            // the source has none). Each attribute owns its own stream so we can CopyFrom directly.
            var attributes = BuildVertexLayout(source, Allocator.Temp);
            writable.SetVertexBufferParams(vertexCount, attributes);
            attributes.Dispose();

            int stream = 0;

            // Positions.
            writable.GetVertexData<float3>(stream++).CopyFrom(source.Vertices);

            // Normals (always present in the layout; zero-fill when the source has none).
            var normalStream = writable.GetVertexData<float3>(stream++);
            if (source.HasNormals)
                normalStream.CopyFrom(source.Normals);
            else
                for (int i = 0; i < vertexCount; i++) normalStream[i] = float3.zero;

            if (source.HasChannelUVs)
                writable.GetVertexData<float2>(stream++).CopyFrom(source.ChannelUVs);

            if (source.HasSourceUV0)
                writable.GetVertexData<float2>(stream++).CopyFrom(source.SourceUV0);

            WriteIndices(writable, source.Triangles, indexCount, indexFormat);

            writable.subMeshCount = 1;
            writable.SetSubMesh(0, new SubMeshDescriptor(0, indexCount, MeshTopology.Triangles));

            var mesh = new Mesh();
            if (!string.IsNullOrEmpty(name))
                mesh.name = name;

            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh,
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);

            mesh.RecalculateBounds();
            if (!source.HasNormals)
                mesh.RecalculateNormals();

            return mesh;
        }

        /// <summary>
        /// Builds a collision-only <see cref="UnityEngine.Mesh"/> (positions + indices, no normals
        /// or UVs) suitable for <c>MeshCollider.sharedMesh</c>. UE equivalent:
        /// <c>FMeshData::ConvertToTriMeshCollisionData</c>.
        /// </summary>
        public static Mesh ToCollisionMesh(in MeshData source, string name = null)
        {
            int vertexCount = source.VertexCount;
            int indexCount = source.TriangleCount * 3;
            var indexFormat = vertexCount > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;

            var meshDataArray = Mesh.AllocateWritableMeshData(1);
            Mesh.MeshData writable = meshDataArray[0];

            var attributes = new NativeArray<VertexAttributeDescriptor>(1, Allocator.Temp);
            attributes[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, stream: 0);
            writable.SetVertexBufferParams(vertexCount, attributes);
            attributes.Dispose();

            writable.GetVertexData<float3>(stream: 0).CopyFrom(source.Vertices);
            WriteIndices(writable, source.Triangles, indexCount, indexFormat);

            writable.subMeshCount = 1;
            writable.SetSubMesh(0, new SubMeshDescriptor(0, indexCount, MeshTopology.Triangles));

            var mesh = new Mesh();
            if (!string.IsNullOrEmpty(name))
                mesh.name = name;

            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh,
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Reads a <see cref="UnityEngine.Mesh"/> into a freshly allocated <see cref="MeshData"/>.
        /// Reads positions, triangles (submesh 0), normals (if present) and UV0 into
        /// <see cref="MeshData.SourceUV0"/>. Used to build test inputs and round-trip tests.
        /// Caller owns the result and must <c>Dispose()</c> it.
        /// </summary>
        public static MeshData FromUnityMesh(Mesh mesh, Allocator allocator)
        {
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var uv0 = mesh.uv;
            var indices = mesh.triangles;

            int vertexCount = vertices.Length;
            int triangleCount = indices.Length / 3;
            bool hasNormals = normals != null && normals.Length == vertexCount;
            bool hasUV0 = uv0 != null && uv0.Length == vertexCount;

            var data = MeshData.Allocate(
                vertexCount, triangleCount, allocator,
                withNormals: hasNormals,
                withChannelUVs: false,
                withSourceUV0: hasUV0,
                withBaseIDs: false);

            for (int i = 0; i < vertexCount; i++)
            {
                data.Vertices[i] = vertices[i];
                if (hasNormals) data.Normals[i] = normals[i];
                if (hasUV0) data.SourceUV0[i] = uv0[i];
            }

            for (int t = 0; t < triangleCount; t++)
            {
                int b = t * 3;
                data.Triangles[t] = new int3(indices[b], indices[b + 1], indices[b + 2]);
            }

            return data;
        }

        static NativeArray<VertexAttributeDescriptor> BuildVertexLayout(in MeshData source, Allocator allocator)
        {
            // Each attribute uses its own stream so we can CopyFrom the matching NativeArray directly.
            // Position + Normal are always present (Normal so RecalculateNormals can run if needed).
            int count = 2; // position + normal
            if (source.HasChannelUVs) count++;
            if (source.HasSourceUV0) count++;

            var attributes = new NativeArray<VertexAttributeDescriptor>(count, allocator);
            int i = 0;
            int stream = 0;

            attributes[i++] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, stream++);
            attributes[i++] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, stream++);

            if (source.HasChannelUVs)
                attributes[i++] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream++);

            if (source.HasSourceUV0)
                attributes[i++] = new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2, stream++);

            return attributes;
        }

        static void WriteIndices(Mesh.MeshData writable, NativeArray<int3> triangles, int indexCount, IndexFormat format)
        {
            writable.SetIndexBufferParams(indexCount, format);

            if (format == IndexFormat.UInt32)
            {
                var dst = writable.GetIndexData<int>();
                for (int t = 0; t < triangles.Length; t++)
                {
                    int3 tri = triangles[t];
                    int b = t * 3;
                    dst[b] = tri.x;
                    dst[b + 1] = tri.y;
                    dst[b + 2] = tri.z;
                }
            }
            else
            {
                var dst = writable.GetIndexData<ushort>();
                for (int t = 0; t < triangles.Length; t++)
                {
                    int3 tri = triangles[t];
                    int b = t * 3;
                    dst[b] = (ushort)tri.x;
                    dst[b + 1] = (ushort)tri.y;
                    dst[b + 2] = (ushort)tri.z;
                }
            }
        }
    }
}
