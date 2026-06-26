using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityMeshSimplifier;

namespace Fca.MeshTerrain.Streaming
{
    /// <summary>One baked LOD: render-ready geometry + its (simplified) weight side-car.</summary>
    public struct LodMesh
    {
        public MeshData Mesh;
        public WeightLayerSet Weights;
    }

    /// <summary>
    /// Bakes a section's render LOD chain on a <b>worker thread</b> — the heavy work that previously ran in
    /// the main-thread present (the profiler showed LOD simplify at ~68 ms/section, re-run on every present
    /// even for cached tiles). Moving it into the cook makes it run <b>once</b> per tile and lets the cache
    /// store the result; a baked-tile reload then does zero simplification and the present is upload-only.
    ///
    /// <para>Thread-safe: the vendored <see cref="MeshSimplifier"/> is fed from raw arrays (not a
    /// <see cref="UnityEngine.Mesh"/>), so no <c>Initialize(Mesh)</c>/<c>ToMesh()</c> is used. Weights ride in
    /// the simplifier's UV channels so decimation stays attribute-aware (parity with <c>02 §7</c>), then are
    /// unpacked back into a per-LOD <see cref="WeightLayerSet"/>.</para>
    /// </summary>
    public static class SectionLODBaker
    {
        const int MaxWeightLayers = 24; // 6 Vector4 UV groups, matching the channel/packing cap.

        /// <summary>
        /// Produces the LOD chain for <paramref name="renderMesh"/> (already skirt-applied). LOD0 is a copy of
        /// the input; each subsequent LOD is simplified to its quality. Caller owns every returned
        /// <see cref="LodMesh"/> (Dispose mesh + weights).
        /// </summary>
        public static LodMesh[] Bake(in MeshData renderMesh, WeightLayerSet renderWeights, float[] qualities, Allocator allocator)
        {
            float[] q = (qualities != null && qualities.Length > 0) ? qualities : new[] { 1f };
            var lods = new LodMesh[q.Length];

            // LOD0: a straight copy (no simplification).
            lods[0] = new LodMesh
            {
                Mesh = Copy(renderMesh, allocator),
                Weights = CopyWeights(renderWeights, renderMesh.VertexCount, allocator),
            };

            if (q.Length == 1)
                return lods;

            // Pre-pack weights into Vector4 UV groups once; reused as the simplifier input for every LOD.
            int layerCount = renderWeights?.LayerCount ?? 0;
            int groups = (layerCount + 3) / 4;
            Vector4[][] weightUV = PackWeightUVs(renderWeights, renderMesh.VertexCount, layerCount, groups);

            for (int i = 1; i < q.Length; i++)
                lods[i] = SimplifyLod(renderMesh, renderWeights, layerCount, groups, weightUV, math.clamp(q[i], 0.01f, 1f), allocator);

            return lods;
        }

        /// <summary>Simplifies <paramref name="src"/> to <paramref name="quality"/> once (geometry + weights),
        /// returning a fresh <see cref="MeshData"/> (+ weights). Used for the baked collision mesh. Thread-safe.</summary>
        public static LodMesh SimplifyOnce(in MeshData src, WeightLayerSet srcWeights, float quality, Allocator allocator)
        {
            int layerCount = srcWeights?.LayerCount ?? 0;
            int groups = (layerCount + 3) / 4;
            Vector4[][] weightUV = PackWeightUVs(srcWeights, src.VertexCount, layerCount, groups);
            return SimplifyLod(src, srcWeights, layerCount, groups, weightUV, math.clamp(quality, 0.01f, 1f), allocator);
        }

        static LodMesh SimplifyLod(in MeshData src, WeightLayerSet srcWeights, int layerCount, int groups,
            Vector4[][] weightUV, float quality, Allocator allocator)
        {
            int vCount = src.VertexCount;
            var simplifier = new MeshSimplifier
            {
                SimplificationOptions = new SimplificationOptions
                {
                    PreserveBorderEdges = true,
                    PreserveUVSeamEdges = true,
                    PreserveUVFoldoverEdges = true,
                    PreserveSurfaceCurvature = false,
                    EnableSmartLink = true,
                    VertexLinkDistance = 1e-6,
                    MaxIterationCount = 100,
                    Agressiveness = 7.0,
                    ManualUVComponentCount = false,
                    UVComponentCount = 4,
                },
            };

            // Geometry + attributes as arrays (no UnityEngine.Mesh).
            var verts = new Vector3[vCount];
            for (int v = 0; v < vCount; v++) verts[v] = (Vector3)(float3)src.Vertices[v];
            simplifier.Vertices = verts;

            if (src.HasNormals)
            {
                var normals = new Vector3[vCount];
                for (int v = 0; v < vCount; v++) normals[v] = (Vector3)(float3)src.Normals[v];
                simplifier.Normals = normals;
            }

            // Channel UVs ride in UV0 (so the atlas mapping is preserved through decimation).
            if (src.HasChannelUVs)
            {
                var uv0 = new List<Vector2>(vCount);
                for (int v = 0; v < vCount; v++) { float2 u = src.ChannelUVs[v]; uv0.Add(new Vector2(u.x, u.y)); }
                simplifier.SetUVs(0, uv0);
            }
            // Source UV0 in UV1.
            if (src.HasSourceUV0)
            {
                var uv1 = new List<Vector2>(vCount);
                for (int v = 0; v < vCount; v++) { float2 u = src.SourceUV0[v]; uv1.Add(new Vector2(u.x, u.y)); }
                simplifier.SetUVs(1, uv1);
            }
            // Weights in UV2.. so the quadric stays attribute-aware (channels don't bleed across LODs).
            for (int g = 0; g < groups; g++)
                simplifier.SetUVs(2 + g, weightUV[g]);

            var tris = new int[src.TriangleCount * 3];
            for (int t = 0; t < src.TriangleCount; t++)
            {
                int3 tri = src.Triangles[t];
                tris[t * 3 + 0] = tri.x; tris[t * 3 + 1] = tri.y; tris[t * 3 + 2] = tri.z;
            }
            simplifier.AddSubMeshTriangles(tris);

            simplifier.SimplifyMesh(quality);

            // Read back.
            Vector3[] outVerts = simplifier.Vertices;
            Vector3[] outNormals = simplifier.Normals;
            int[] outTris = simplifier.GetSubMeshTriangles(0);
            int outV = outVerts.Length;
            int outT = outTris.Length / 3;

            var dst = MeshData.Allocate(outV, outT, allocator,
                withNormals: src.HasNormals, withChannelUVs: src.HasChannelUVs,
                withSourceUV0: src.HasSourceUV0, withBaseIDs: false);

            for (int v = 0; v < outV; v++)
                dst.Vertices[v] = new float3(outVerts[v].x, outVerts[v].y, outVerts[v].z);
            if (src.HasNormals && outNormals != null && outNormals.Length == outV)
                for (int v = 0; v < outV; v++)
                    dst.Normals[v] = new float3(outNormals[v].x, outNormals[v].y, outNormals[v].z);

            if (src.HasChannelUVs) ReadUV2(simplifier, 0, outV, dst.ChannelUVs);
            if (src.HasSourceUV0) ReadUV2(simplifier, 1, outV, dst.SourceUV0);

            for (int t = 0; t < outT; t++)
                dst.Triangles[t] = new int3(outTris[t * 3], outTris[t * 3 + 1], outTris[t * 3 + 2]);

            // Unpack the simplified weights back into a side-car.
            WeightLayerSet dstWeights = null;
            if (layerCount > 0)
            {
                dstWeights = new WeightLayerSet(allocator);
                var uvScratch = new List<Vector4>(outV);
                var layers = new NativeArray<float>[layerCount];
                for (int l = 0; l < layerCount; l++)
                    layers[l] = dstWeights.InitializeLayer(srcWeights.LayerNames[l], outV);

                for (int g = 0; g < groups; g++)
                {
                    uvScratch.Clear();
                    simplifier.GetUVs(2 + g, uvScratch);
                    for (int v = 0; v < outV && v < uvScratch.Count; v++)
                    {
                        Vector4 packed = uvScratch[v];
                        for (int c = 0; c < 4; c++)
                        {
                            int layer = g * 4 + c;
                            if (layer >= layerCount) break;
                            layers[layer][v] = packed[c];
                        }
                    }
                }
            }

            return new LodMesh { Mesh = dst, Weights = dstWeights };
        }

        static void ReadUV2(MeshSimplifier simplifier, int channel, int count, NativeArray<float2> dst)
        {
            var uvs = new List<Vector2>(count);
            simplifier.GetUVs(channel, uvs);
            for (int v = 0; v < count && v < uvs.Count; v++)
                dst[v] = new float2(uvs[v].x, uvs[v].y);
        }

        static Vector4[][] PackWeightUVs(WeightLayerSet weights, int vCount, int layerCount, int groups)
        {
            var packed = new Vector4[groups][];
            for (int g = 0; g < groups; g++)
            {
                var arr = new Vector4[vCount];
                for (int v = 0; v < vCount; v++)
                {
                    var val = Vector4.zero;
                    for (int c = 0; c < 4; c++)
                    {
                        int layer = g * 4 + c;
                        if (layer >= layerCount) break;
                        val[c] = weights.GetLayerByIndex(layer)[v];
                    }
                    arr[v] = val;
                }
                packed[g] = arr;
            }
            return packed;
        }

        static MeshData Copy(in MeshData src, Allocator allocator)
        {
            var dst = MeshData.Allocate(src.VertexCount, src.TriangleCount, allocator,
                src.HasNormals, src.HasChannelUVs, src.HasSourceUV0, src.HasBaseIDs);
            for (int v = 0; v < src.VertexCount; v++)
            {
                dst.Vertices[v] = src.Vertices[v];
                if (src.HasNormals) dst.Normals[v] = src.Normals[v];
                if (src.HasChannelUVs) dst.ChannelUVs[v] = src.ChannelUVs[v];
                if (src.HasSourceUV0) dst.SourceUV0[v] = src.SourceUV0[v];
            }
            for (int t = 0; t < src.TriangleCount; t++)
            {
                dst.Triangles[t] = src.Triangles[t];
                if (src.HasBaseIDs) dst.BaseIDLayer[t] = src.BaseIDLayer[t];
            }
            return dst;
        }

        static WeightLayerSet CopyWeights(WeightLayerSet src, int vCount, Allocator allocator)
        {
            if (src == null || src.LayerCount == 0) return null;
            var result = new WeightLayerSet(allocator);
            for (int i = 0; i < src.LayerCount; i++)
            {
                var s = src.GetLayerByIndex(i);
                var d = result.InitializeLayer(src.LayerNames[i], vCount);
                for (int v = 0; v < vCount; v++) d[v] = s[v];
            }
            return result;
        }
    }
}
