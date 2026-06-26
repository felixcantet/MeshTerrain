using System;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Result of rasterizing a section's channels: a single-channel image per weight layer (slice i =
    /// global channel i), plus the <see cref="ChannelTable"/> describing which slice each channel
    /// landed in and the per-section texcoord metric for the material.
    ///
    /// The CPU backend stores a <see cref="Texture2DArray"/>; the GPU backend (Phase 4b) stores a
    /// <see cref="RenderTexture"/> array (kept live, no readback). Both expose the array slices as a
    /// single <see cref="Texture"/> so the material/<c>CompiledSection</c> path is backend-agnostic.
    /// </summary>
    public sealed class ChannelRasterResult : IDisposable
    {
        public Texture Texture;
        public ChannelTable Table;
        public float2 TexcoordMetrics;

        public void Dispose()
        {
            if (Texture != null)
            {
                if (Texture is RenderTexture rt)
                {
                    rt.Release();
                    if (Application.isPlaying) UnityEngine.Object.Destroy(rt);
                    else UnityEngine.Object.DestroyImmediate(rt);
                }
                else if (Application.isPlaying) UnityEngine.Object.Destroy(Texture);
                else UnityEngine.Object.DestroyImmediate(Texture);
                Texture = null;
            }
        }
    }

    /// <summary>
    /// Unity-object-free result of a thread-safe channel rasterization: a tightly-packed R8 atlas blob
    /// (slice 0 first, then slice 1, …) plus the packing table + texcoord metric. Built on a worker thread by
    /// <see cref="ChannelRasterizerCPU.RenderToBytes"/>; <see cref="ToTexture2DArray"/> uploads it on the
    /// main thread. This is what lets the cook run async (<c>doc/08 §8</c>).
    /// </summary>
    public struct ChannelRasterBytes
    {
        public int Resolution;
        public int Slices;
        public byte[] R8;
        public ChannelTable Table;
        public Unity.Mathematics.float2 TexcoordMetrics;

        public bool HasData => R8 != null && R8.Length > 0;

        /// <summary>Builds the R8 <see cref="Texture2DArray"/> from the blob. <b>Main thread only.</b></summary>
        public Texture2DArray ToTexture2DArray(string name = "SectionChannels")
        {
            int res = Resolution;
            int slices = math.max(1, Slices);
            var tex = new Texture2DArray(res, res, slices, TextureFormat.R8, mipChain: true, linear: true)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            int sliceBytes = res * res;
            var px = new Color32[sliceBytes];
            for (int s = 0; s < slices; s++)
            {
                int offset = s * sliceBytes;
                for (int i = 0; i < sliceBytes; i++)
                {
                    byte b = R8[offset + i];
                    px[i] = new Color32(b, b, b, 255);
                }
                tex.SetPixels32(px, s);
            }
            tex.Apply(updateMipmaps: true);
            return tex;
        }
    }

    /// <summary>
    /// CPU port of UE's channel rasterization pipeline
    /// (<c>MeshPartitionMakeSectionChannels.usf</c>, <c>MeshPartitionBorderFill.usf</c>): for each
    /// section it rasterizes per-vertex weight layers into a texture in the channel-UV domain, then
    /// dilates the covered region (border fill) and optionally runs a pull-push gutter fill so
    /// bilinear/mip sampling does not bleed black across UV islands.
    ///
    /// This is the backend selected for Phase 4a; a GPU/compute backend (Phase 4b) reuses the same
    /// UV/packing/material code and produces an equivalent texture.
    /// </summary>
    public static class ChannelRasterizerCPU
    {
        /// <summary>
        /// Rasterizes <paramref name="weights"/> over <paramref name="section"/> (which must carry
        /// <see cref="MeshData.ChannelUVs"/>). Returns one texture slice per weight layer. When
        /// <paramref name="enableGutterFill"/> is false, only the kernel-3 border fill runs (the
        /// pull-push pass is "optional first" per the roadmap). Caller owns the result.
        /// </summary>
        public static ChannelRasterResult Render(
            in MeshData section,
            WeightLayerSet weights,
            in SectionDomainMapping mapping,
            bool enableGutterFill = true)
        {
            ChannelRasterBytes bytes = RenderToBytes(section, weights, mapping, enableGutterFill);
            return new ChannelRasterResult
            {
                Texture = bytes.ToTexture2DArray(),
                Table = bytes.Table,
                TexcoordMetrics = bytes.TexcoordMetrics,
            };
        }

        /// <summary>
        /// Pure (Unity-object-free, <b>thread-safe</b>) rasterization: runs passes 1–3 and packs the result
        /// into a tightly-packed R8 byte blob + table + metrics, without allocating a <see cref="Texture2DArray"/>.
        /// This is the path the async cook (<c>doc/08 §8</c>) calls on a worker thread; the texture is built
        /// later on the main thread via <see cref="ChannelRasterBytes.ToTexture2DArray"/>.
        /// </summary>
        public static ChannelRasterBytes RenderToBytes(
            in MeshData section,
            WeightLayerSet weights,
            in SectionDomainMapping mapping,
            bool enableGutterFill = true)
        {
            if (!section.HasChannelUVs)
                throw new InvalidOperationException("Section must have ChannelUVs generated before rasterization.");

            int layerCount = weights?.LayerCount ?? 0;
            int res = math.max(4, mapping.ImageResolution);
            int sliceCount = math.max(1, layerCount);

            // Per-slice signal + a shared coverage mask (coverage is identical across slices since
            // every layer is defined on the same vertices).
            var signal = new float[sliceCount][];
            for (int s = 0; s < sliceCount; s++) signal[s] = new float[res * res];
            var mask = new bool[res * res];

            // ---- Pass 1: rasterize triangles in UV space, interpolate per-vertex weights. ----
            for (int t = 0; t < section.TriangleCount; t++)
            {
                int3 tri = section.Triangles[t];
                float2 uv0 = section.ChannelUVs[tri.x];
                float2 uv1 = section.ChannelUVs[tri.y];
                float2 uv2 = section.ChannelUVs[tri.z];
                RasterizeTriangle(section, weights, layerCount, res, signal, mask, tri, uv0, uv1, uv2);
            }

            // ---- Pass 2: border fill (nearest covered-texel dilation, kernel 3). ----
            BorderFill(signal, mask, sliceCount, res);

            // ---- Pass 3 (optional): pull-push gutter fill over a mip pyramid. ----
            if (enableGutterFill)
                PullPushFill(signal, mask, sliceCount, res);

            // ---- Pack into a tightly-packed R8 blob (slice 0 first, then slice 1, …). ----
            var blob = new byte[res * res * sliceCount];
            for (int s = 0; s < sliceCount; s++)
            {
                var src = signal[s];
                int offset = s * res * res;
                for (int i = 0; i < src.Length; i++)
                    blob[offset + i] = (byte)math.clamp((int)math.round(src[i] * 255f), 0, 255);
            }

            // Slice mapping: channel i -> slice i (1:1 here). Absent if no layers.
            var sliceForChannel = new int[layerCount];
            for (int c = 0; c < layerCount; c++) sliceForChannel[c] = c;

            return new ChannelRasterBytes
            {
                Resolution = res,
                Slices = sliceCount,
                R8 = blob,
                Table = ChannelTable.Build(sliceForChannel),
                TexcoordMetrics = mapping.TexcoordMetrics,
            };
        }

        static void RasterizeTriangle(
            in MeshData section, WeightLayerSet weights, int layerCount, int res,
            float[][] signal, bool[] mask, int3 tri, float2 uv0, float2 uv1, float2 uv2)
        {
            // UV [0,1] -> pixel center space.
            float2 p0 = uv0 * res, p1 = uv1 * res, p2 = uv2 * res;

            int minX = math.clamp((int)math.floor(math.min(p0.x, math.min(p1.x, p2.x))), 0, res - 1);
            int maxX = math.clamp((int)math.ceil(math.max(p0.x, math.max(p1.x, p2.x))), 0, res - 1);
            int minY = math.clamp((int)math.floor(math.min(p0.y, math.min(p1.y, p2.y))), 0, res - 1);
            int maxY = math.clamp((int)math.ceil(math.max(p0.y, math.max(p1.y, p2.y))), 0, res - 1);

            float denom = EdgeFn(p0, p1, p2);
            if (math.abs(denom) < 1e-12f) return;
            float inv = 1f / denom;

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float2 p = new float2(x + 0.5f, y + 0.5f);
                    float w0 = EdgeFn(p1, p2, p) * inv;
                    float w1 = EdgeFn(p2, p0, p) * inv;
                    float w2 = EdgeFn(p0, p1, p) * inv;
                    if (w0 < 0f || w1 < 0f || w2 < 0f) continue;

                    int idx = y * res + x;
                    mask[idx] = true;
                    for (int c = 0; c < layerCount; c++)
                    {
                        var layer = weights.GetLayerByIndex(c);
                        signal[c][idx] = w0 * layer[tri.x] + w1 * layer[tri.y] + w2 * layer[tri.z];
                    }
                }
            }
        }

        static float EdgeFn(float2 a, float2 b, float2 c)
            => (c.x - a.x) * (b.y - a.y) - (c.y - a.y) * (b.x - a.x);

        static void BorderFill(float[][] signal, bool[] mask, int sliceCount, int res)
        {
            const int kernel = 3;
            var filled = (bool[])mask.Clone();
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    int idx = y * res + x;
                    if (mask[idx]) continue;

                    int bestDistSqr = (kernel + 1) * (kernel + 1);
                    int samples = 0;
                    var accum = new float[sliceCount];

                    int x0 = math.max(x - kernel, 0), x1 = math.min(x + kernel, res - 1);
                    int y0 = math.max(y - kernel, 0), y1 = math.min(y + kernel, res - 1);
                    for (int sy = y0; sy <= y1; sy++)
                    {
                        for (int sx = x0; sx <= x1; sx++)
                        {
                            int sIdx = sy * res + sx;
                            if (!mask[sIdx]) continue;
                            int d = (sx - x) * (sx - x) + (sy - y) * (sy - y);
                            if (d > bestDistSqr) continue;
                            if (d < bestDistSqr) { bestDistSqr = d; samples = 0; Array.Clear(accum, 0, sliceCount); }
                            for (int s = 0; s < sliceCount; s++) accum[s] += signal[s][sIdx];
                            samples++;
                        }
                    }

                    if (samples > 0)
                    {
                        filled[idx] = true;
                        for (int s = 0; s < sliceCount; s++) signal[s][idx] = accum[s] / samples;
                    }
                }
            }
            Array.Copy(filled, mask, mask.Length);
        }

        /// <summary>
        /// Pull-push inpainting: build a mip pyramid by mask-weighted downsample (pull), then fill
        /// holes in finer levels from the coarser ones (push). Direct CPU port of UE PullCS/PushCS.
        /// </summary>
        static void PullPushFill(float[][] signal, bool[] mask, int sliceCount, int res)
        {
            int levels = 1;
            while ((res >> levels) >= 1) levels++;

            var sig = new float[levels][][];
            var cov = new float[levels][];
            var sizes = new int[levels];

            // Level 0 from current state.
            sizes[0] = res;
            sig[0] = signal;
            cov[0] = new float[res * res];
            for (int i = 0; i < res * res; i++) cov[0][i] = mask[i] ? 1f : 0f;

            // Pull: coarsen.
            for (int l = 1; l < levels; l++)
            {
                int srcRes = sizes[l - 1];
                int dstRes = math.max(1, srcRes / 2);
                sizes[l] = dstRes;
                sig[l] = new float[sliceCount][];
                for (int s = 0; s < sliceCount; s++) sig[l][s] = new float[dstRes * dstRes];
                cov[l] = new float[dstRes * dstRes];

                for (int y = 0; y < dstRes; y++)
                {
                    for (int x = 0; x < dstRes; x++)
                    {
                        float covSum = 0;
                        var sSum = new float[sliceCount];
                        for (int dy = 0; dy < 2; dy++)
                        {
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int sx = math.min(x * 2 + dx, srcRes - 1);
                                int sy = math.min(y * 2 + dy, srcRes - 1);
                                int sIdx = sy * srcRes + sx;
                                float m = cov[l - 1][sIdx];
                                covSum += m;
                                for (int s = 0; s < sliceCount; s++) sSum[s] += m * sig[l - 1][s][sIdx];
                            }
                        }
                        int dIdx = y * dstRes + x;
                        if (covSum > 0)
                        {
                            for (int s = 0; s < sliceCount; s++) sig[l][s][dIdx] = sSum[s] / covSum;
                            cov[l][dIdx] = covSum * 0.25f;
                        }
                    }
                }
            }

            // Push: backfill holes in finer levels from coarser ones.
            for (int l = levels - 2; l >= 0; l--)
            {
                int dstRes = sizes[l];
                int srcRes = sizes[l + 1];
                for (int y = 0; y < dstRes; y++)
                {
                    for (int x = 0; x < dstRes; x++)
                    {
                        int dIdx = y * dstRes + x;
                        if (cov[l][dIdx] > 0f) continue; // already covered

                        int sx = math.min(x / 2, srcRes - 1);
                        int sy = math.min(y / 2, srcRes - 1);
                        int sIdx = sy * srcRes + sx;
                        for (int s = 0; s < sliceCount; s++) sig[l][s][dIdx] = sig[l + 1][s][sIdx];
                        cov[l][dIdx] = 1f;
                    }
                }
            }
        }
    }
}
