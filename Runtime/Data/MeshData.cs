using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// NativeArray-backed pivot mesh format. Unity/Burst port of Unreal's
    /// <c>UE::MeshPartition::FMeshData</c> (see <c>doc/source/.../MeshPartitionMeshData.h</c>).
    ///
    /// Unlike the UE original, this format intentionally drops the free-list / ref-count
    /// machinery (<c>VertexRefCount</c>, <c>TriangleRefCount</c>, <c>FreeVertices</c>,
    /// <c>FreeTriangles</c>). Those exist to support in-place topological edits during UE's
    /// modifier pipeline. The Unity roadmap uses a rebuild-per-section model (see
    /// <c>doc/06_BURST_AND_COMPUTE.md §4</c>), so all buffers here are plain contiguous arrays.
    /// An append/builder path can be introduced alongside the modifier stack (Phase 2) if needed.
    ///
    /// Weight layers (UE <c>FMeshData.WeightLayers : TMap&lt;FName, TArray&lt;float&gt;&gt;</c>) are
    /// deliberately NOT part of this struct: managed maps/strings cannot cross into Burst jobs.
    /// They live in the managed side-car <see cref="WeightLayerSet"/>, which hands jobs raw
    /// <see cref="NativeArray{T}"/> layers indexed by an int channel index.
    /// </summary>
    public struct MeshData : IDisposable
    {
        // --- Geometry ---

        /// <summary>Vertex positions. UE <c>FMeshData.Vertices</c> (FVector3d → float3).</summary>
        public NativeArray<float3> Vertices;

        /// <summary>Triangle vertex-index triplets. UE <c>FMeshData.Triangles</c> (FIndex3i).</summary>
        public NativeArray<int3> Triangles;

        // --- Per-vertex attributes ---

        /// <summary>Per-vertex normals. UE <c>FMeshData.Normals</c>. May be default (not created).</summary>
        public NativeArray<float3> Normals;

        /// <summary>
        /// Auto-generated channel/atlas UVs. UE <c>FMeshData.ChannelUVs</c>.
        /// Populated by the channel pipeline (Phase 4); may be default until then.
        /// </summary>
        public NativeArray<float2> ChannelUVs;

        /// <summary>
        /// Single source UV channel passed through from the imported mesh.
        /// UE supports up to 7 (<c>FMeshData.SourceUVChannels</c>); Phase 0 keeps one and grows later.
        /// May be default (not created).
        /// </summary>
        public NativeArray<float2> SourceUV0;

        // --- Per-triangle attributes ---

        /// <summary>
        /// Per-triangle base id: which base modifier produced the triangle.
        /// UE <c>FMeshData.BaseIDLayer</c>. May be default (not created).
        /// </summary>
        public NativeArray<int> BaseIDLayer;

        public int VertexCount => Vertices.IsCreated ? Vertices.Length : 0;
        public int TriangleCount => Triangles.IsCreated ? Triangles.Length : 0;

        public bool HasNormals => Normals.IsCreated && Normals.Length == VertexCount;
        public bool HasChannelUVs => ChannelUVs.IsCreated && ChannelUVs.Length == VertexCount;
        public bool HasSourceUV0 => SourceUV0.IsCreated && SourceUV0.Length == VertexCount;
        public bool HasBaseIDs => BaseIDLayer.IsCreated && BaseIDLayer.Length == TriangleCount;

        /// <summary>
        /// Allocates the core geometry buffers (positions + triangles) and, optionally, the
        /// common per-vertex attribute buffers. Attribute buffers left out can be assigned later.
        /// </summary>
        public static MeshData Allocate(
            int vertexCount,
            int triangleCount,
            Allocator allocator,
            bool withNormals = true,
            bool withChannelUVs = false,
            bool withSourceUV0 = false,
            bool withBaseIDs = false)
        {
            var options = NativeArrayOptions.UninitializedMemory;
            var data = new MeshData
            {
                Vertices = new NativeArray<float3>(vertexCount, allocator, options),
                Triangles = new NativeArray<int3>(triangleCount, allocator, options),
            };

            if (withNormals)
                data.Normals = new NativeArray<float3>(vertexCount, allocator, options);
            if (withChannelUVs)
                data.ChannelUVs = new NativeArray<float2>(vertexCount, allocator, options);
            if (withSourceUV0)
                data.SourceUV0 = new NativeArray<float2>(vertexCount, allocator, options);
            if (withBaseIDs)
                data.BaseIDLayer = new NativeArray<int>(triangleCount, allocator, options);

            return data;
        }

        public void Dispose()
        {
            if (Vertices.IsCreated) Vertices.Dispose();
            if (Triangles.IsCreated) Triangles.Dispose();
            if (Normals.IsCreated) Normals.Dispose();
            if (ChannelUVs.IsCreated) ChannelUVs.Dispose();
            if (SourceUV0.IsCreated) SourceUV0.Dispose();
            if (BaseIDLayer.IsCreated) BaseIDLayer.Dispose();
        }
    }
}
