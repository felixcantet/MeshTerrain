using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// GPU/compute backend for channel rasterization (Phase 4b). Mirrors
    /// <see cref="ChannelRasterizerCPU"/> but produces the atlas on the GPU and keeps it live as a
    /// <see cref="RenderTexture"/> array (no readback): the section's triangles are drawn in the
    /// channel-UV domain (<c>ChannelRaster.shader</c>, port of UE <c>DrawUVDomainVS/PS</c>), then a
    /// kernel-3 border fill and a pull-push gutter fill run as compute passes
    /// (<c>ChannelBorderFill.compute</c>, <c>ChannelPullPush.compute</c>).
    ///
    /// Resources live under <c>Runtime/Resources/MeshTerrain</c> so they load in editor and player.
    /// Returns null when compute shaders are unsupported (callers fall back to the CPU backend).
    /// </summary>
    public static class ChannelRasterizerGPU
    {
        const string RasterShaderPath = "MeshTerrain/ChannelRaster";
        const string BorderFillPath = "MeshTerrain/ChannelBorderFill";
        const string PullPushPath = "MeshTerrain/ChannelPullPush";

        static Shader s_rasterShader;
        static ComputeShader s_borderFill;
        static ComputeShader s_pullPush;
        static Material s_rasterMaterial;

        public static bool IsSupported => SystemInfo.supportsComputeShaders;

        static bool EnsureResources()
        {
            if (s_rasterShader == null) s_rasterShader = Resources.Load<Shader>(RasterShaderPath);
            if (s_borderFill == null) s_borderFill = Resources.Load<ComputeShader>(BorderFillPath);
            if (s_pullPush == null) s_pullPush = Resources.Load<ComputeShader>(PullPushPath);
            if (s_rasterShader != null && s_rasterMaterial == null)
                s_rasterMaterial = new Material(s_rasterShader) { hideFlags = HideFlags.HideAndDontSave };
            return s_rasterShader != null && s_borderFill != null && s_pullPush != null && s_rasterMaterial != null;
        }

        /// <summary>
        /// GPU equivalent of <see cref="ChannelRasterizerCPU.Render"/>. Returns a result whose
        /// <see cref="ChannelRasterResult.Texture"/> is a live <see cref="RenderTexture"/> array
        /// (one R slice per channel), or null if compute/resources are unavailable.
        /// </summary>
        public static ChannelRasterResult Render(
            in MeshData section,
            WeightLayerSet weights,
            in SectionDomainMapping mapping,
            bool enableGutterFill = true)
        {
            if (!IsSupported || !EnsureResources())
                return null;
            if (!section.HasChannelUVs)
                throw new InvalidOperationException("Section must have ChannelUVs generated before rasterization.");

            int layerCount = weights?.LayerCount ?? 0;
            int sliceCount = math.max(1, layerCount);
            int res = math.max(4, mapping.ImageResolution);
            int vertexCount = section.VertexCount;
            int indexCount = section.TriangleCount * 3;

            // --- Upload geometry + weights to GraphicsBuffers. ---
            var uvBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, math.max(1, vertexCount), sizeof(float) * 2);
            var weightBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, math.max(1, vertexCount * sliceCount), sizeof(float));
            var indexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, math.max(1, indexCount), sizeof(int));

            var uvData = new NativeArray<float2>(vertexCount, Allocator.Temp);
            for (int v = 0; v < vertexCount; v++) uvData[v] = section.ChannelUVs[v];
            uvBuffer.SetData(uvData);
            uvData.Dispose();

            var weightData = new float[vertexCount * sliceCount];
            for (int c = 0; c < layerCount; c++)
            {
                var layer = weights.GetLayerByIndex(c);
                for (int v = 0; v < vertexCount; v++)
                    weightData[v * sliceCount + c] = layer[v];
            }
            weightBuffer.SetData(weightData);

            var indices = new int[indexCount];
            for (int t = 0; t < section.TriangleCount; t++)
            {
                int3 tri = section.Triangles[t];
                indices[t * 3] = tri.x; indices[t * 3 + 1] = tri.y; indices[t * 3 + 2] = tri.z;
            }
            indexBuffer.SetData(indices);

            // --- Final result RT array (one R slice per channel). ---
            var resultArray = NewRTArray(res, sliceCount, "SectionChannelsGPU");

            var signal = NewRT(res, "ChannelSignal");
            var mask = NewRT(res, "ChannelMask");
            var maskFilled = NewRT(res, "ChannelMaskFilled"); // border-fill writes updated coverage here

            var cmd = new CommandBuffer { name = "ChannelRasterizerGPU" };
            try
            {
                int borderKernel = s_borderFill.FindKernel("BorderFill");
                int pullKernel = s_pullPush.FindKernel("Pull");
                int pushKernel = s_pullPush.FindKernel("Push");

                for (int slice = 0; slice < sliceCount; slice++)
                {
                    // Pass 1: rasterize this channel's weights + mask into the two MRTs. No depth
                    // buffer is needed (ZTest Always / ZWrite Off in the shader). Per-slice state
                    // goes through a MaterialPropertyBlock so each recorded draw captures its own
                    // _Slice — setting it on the shared material would make every draw use the last
                    // slice when the command buffer executes.
                    // SetRenderTarget does not accept RenderBufferLoadAction.Clear; clear explicitly
                    // after binding instead. DontCare load (we overwrite via the clear + draw).
                    var mrt = new RenderTargetBinding(
                        new[] { new RenderTargetIdentifier(signal), new RenderTargetIdentifier(mask) },
                        new[] { RenderBufferLoadAction.DontCare, RenderBufferLoadAction.DontCare },
                        new[] { RenderBufferStoreAction.Store, RenderBufferStoreAction.Store },
                        signal, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
                    cmd.SetRenderTarget(mrt);
                    cmd.ClearRenderTarget(false, true, Color.clear);

                    var mpb = new MaterialPropertyBlock();
                    mpb.SetBuffer("_ChannelUVs", uvBuffer);
                    mpb.SetBuffer("_ChannelWeights", weightBuffer);
                    mpb.SetInt("_ChannelCount", sliceCount);
                    mpb.SetInt("_Slice", slice);
                    cmd.DrawProcedural(indexBuffer, Matrix4x4.identity, s_rasterMaterial, 0,
                        MeshTopology.Triangles, indexCount, 1, mpb);

                    // Pass 2: border fill — fills signal in place and writes the UPDATED coverage to
                    // maskFilled (so pull-push uses the same post-border mask as the CPU path).
                    int groups = (res + 7) / 8;
                    cmd.SetComputeIntParams(s_borderFill, "_Resolution", res, res);
                    cmd.SetComputeTextureParam(s_borderFill, borderKernel, "_Mask", mask);
                    cmd.SetComputeTextureParam(s_borderFill, borderKernel, "_RWSignal", signal);
                    cmd.SetComputeTextureParam(s_borderFill, borderKernel, "_RWMaskOut", maskFilled);
                    cmd.DispatchCompute(s_borderFill, borderKernel, groups, groups, 1);

                    // Pass 3 (optional): pull-push gutter fill over a mip pyramid, seeded by the
                    // post-border coverage.
                    if (enableGutterFill)
                        PullPush(cmd, signal, maskFilled, res, pullKernel, pushKernel);

                    // Copy the finished single-channel signal into the result array slice.
                    cmd.CopyTexture(signal, 0, 0, resultArray, slice, 0);
                }

                cmd.GenerateMips(resultArray);
                Graphics.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                cmd.Release();
                RenderTexture.ReleaseTemporary(signal);
                RenderTexture.ReleaseTemporary(mask);
                RenderTexture.ReleaseTemporary(maskFilled);
                uvBuffer.Dispose();
                weightBuffer.Dispose();
                indexBuffer.Dispose();
            }

            var sliceForChannel = new int[layerCount];
            for (int c = 0; c < layerCount; c++) sliceForChannel[c] = c;

            return new ChannelRasterResult
            {
                Texture = resultArray,
                Table = ChannelTable.Build(sliceForChannel),
                TexcoordMetrics = mapping.TexcoordMetrics,
            };
        }

        static void PullPush(CommandBuffer cmd, RenderTexture signal, RenderTexture mask, int res, int pull, int push)
        {
            int levels = 1;
            while ((res >> levels) >= 1) levels++;

            var sig = new RenderTexture[levels];
            var cov = new RenderTexture[levels];
            var sizes = new int[levels];
            sizes[0] = res;
            sig[0] = signal;
            cov[0] = mask;
            for (int l = 1; l < levels; l++)
            {
                sizes[l] = math.max(1, sizes[l - 1] / 2);
                sig[l] = NewRT(sizes[l], $"PullSig{l}");
                cov[l] = NewRT(sizes[l], $"PullMask{l}");
            }

            // Pull: coarsen, mask-weighted.
            for (int l = 1; l < levels; l++)
            {
                int dst = sizes[l];
                int groups = (dst + 7) / 8;
                cmd.SetComputeIntParams(s_pullPush, "_ResolutionPass", dst, dst);
                cmd.SetComputeTextureParam(s_pullPush, pull, "_SignalIn", sig[l - 1]);
                cmd.SetComputeTextureParam(s_pullPush, pull, "_MaskIn", cov[l - 1]);
                cmd.SetComputeTextureParam(s_pullPush, pull, "_SignalOut", sig[l]);
                cmd.SetComputeTextureParam(s_pullPush, pull, "_MaskOut", cov[l]);
                cmd.DispatchCompute(s_pullPush, pull, groups, groups, 1);
            }

            // Push: backfill finer holes from coarser levels.
            for (int l = levels - 2; l >= 0; l--)
            {
                int dst = sizes[l];
                int srcRes = sizes[l + 1];
                int groups = (dst + 7) / 8;
                cmd.SetComputeIntParams(s_pullPush, "_ResolutionPass", dst, dst);
                cmd.SetComputeVectorParam(s_pullPush, "_TexelSize", new Vector4(1f / srcRes, 1f / srcRes, 0, 0));
                cmd.SetComputeTextureParam(s_pullPush, push, "_SignalIn", sig[l + 1]);
                cmd.SetComputeTextureParam(s_pullPush, push, "_MaskIn", cov[l]);
                cmd.SetComputeTextureParam(s_pullPush, push, "_SignalOut", sig[l]);
                cmd.DispatchCompute(s_pullPush, push, groups, groups, 1);
            }

            // Release pyramid temporaries (levels 1..n; level 0 aliases the caller's signal/mask).
            for (int l = 1; l < levels; l++)
            {
                RenderTexture.ReleaseTemporary(sig[l]);
                RenderTexture.ReleaseTemporary(cov[l]);
            }
        }

        static RenderTexture NewRT(int res, string name)
        {
            var rt = RenderTexture.GetTemporary(new RenderTextureDescriptor(res, res, RenderTextureFormat.RFloat, 0)
            {
                enableRandomWrite = true,
                useMipMap = false,
                sRGB = false,
            });
            rt.name = name;
            rt.filterMode = FilterMode.Bilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            if (!rt.IsCreated()) rt.Create();
            return rt;
        }

        static RenderTexture NewRTArray(int res, int slices, string name)
        {
            // RFloat (matching the signal RT) so per-slice CopyTexture has no format mismatch; the
            // material samples .r in [0,1] either way.
            var rt = new RenderTexture(new RenderTextureDescriptor(res, res, RenderTextureFormat.RFloat, 0)
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = slices,
                enableRandomWrite = true,
                useMipMap = true,
                autoGenerateMips = false,
                sRGB = false,
            })
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }
    }
}
