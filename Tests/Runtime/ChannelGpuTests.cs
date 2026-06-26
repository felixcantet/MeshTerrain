using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain.Tests
{
    /// <summary>
    /// GPU channel-rasterizer tests (Phase 4b). The backend keeps the atlas as a live
    /// <see cref="RenderTexture"/> array; these tests read it back only for comparison. They are
    /// skipped (Inconclusive) when the device has no compute support or the package shader resources
    /// cannot be loaded — so headless/CI machines do not produce false failures.
    /// </summary>
    public class ChannelGpuTests
    {
        static void RequireGpu()
        {
            if (!ChannelRasterizerGPU.IsSupported)
                Assert.Ignore("Compute shaders not supported on this device; skipping GPU channel tests.");
        }

        // Reads array slice 0 of a RenderTexture array back into a float[] for assertions.
        static float[] ReadSlice0(RenderTexture rtArray)
        {
            int res = rtArray.width;
            var prev = RenderTexture.active;
            var tmp = RenderTexture.GetTemporary(res, res, 0, RenderTextureFormat.RFloat);
            try
            {
                Graphics.CopyTexture(rtArray, 0, 0, tmp, 0, 0);
                RenderTexture.active = tmp;
                var tex = new Texture2D(res, res, TextureFormat.RFloat, false, true);
                tex.ReadPixels(new Rect(0, 0, res, res), 0, 0);
                tex.Apply();
                var px = tex.GetPixels();
                var outp = new float[px.Length];
                for (int i = 0; i < px.Length; i++) outp[i] = px[i].r;
                Object.DestroyImmediate(tex);
                return outp;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(tmp);
            }
        }

        [Test]
        public void Gpu_ProducesNonEmptyAtlasForPaintedChannel()
        {
            RequireGpu();
            using var source = TestMeshFactory.BuildPlane(8, 40f, Allocator.TempJob);
            using var weights = new WeightLayerSet(Allocator.TempJob);
            var grass = weights.InitializeLayer("Grass", source.VertexCount);
            for (int i = 0; i < source.VertexCount; i++) grass[i] = 1f;

            var channelized = ChannelUVUnwrap.Generate(source, weights, ChannelUVSettings.Default,
                Allocator.TempJob, out var cWeights, out var mapping);
            ChannelRasterResult raster = null;
            try
            {
                raster = ChannelRasterizerGPU.Render(channelized, cWeights, mapping, enableGutterFill: true);
                if (raster == null) Assert.Ignore("GPU backend unavailable (resources not loaded).");

                Assert.IsInstanceOf<RenderTexture>(raster.Texture);
                float max = 0f;
                foreach (var v in ReadSlice0((RenderTexture)raster.Texture)) max = math.max(max, v);
                Assert.Greater(max, 0.9f, "fully-painted channel should rasterize near 1.0 on the GPU");
                Assert.AreEqual(0, raster.Table.GetSlice(0));
            }
            finally { raster?.Dispose(); cWeights?.Dispose(); channelized.Dispose(); }
        }

        [Test]
        public void Gpu_MatchesCpuWithinTolerance()
        {
            RequireGpu();
            using var source = TestMeshFactory.BuildPlane(8, 40f, Allocator.TempJob);
            using var weights = new WeightLayerSet(Allocator.TempJob);
            var grass = weights.InitializeLayer("Grass", source.VertexCount);
            // A gradient so we exercise interpolation, not just a flat fill.
            for (int i = 0; i < source.VertexCount; i++)
                grass[i] = math.saturate(source.Vertices[i].x / 40f);

            var channelized = ChannelUVUnwrap.Generate(source, weights, ChannelUVSettings.Default,
                Allocator.TempJob, out var cWeights, out var mapping);
            ChannelRasterResult gpu = null, cpu = null;
            try
            {
                gpu = ChannelRasterizerGPU.Render(channelized, cWeights, mapping, enableGutterFill: true);
                if (gpu == null) Assert.Ignore("GPU backend unavailable (resources not loaded).");
                cpu = ChannelRasterizerCPU.Render(channelized, cWeights, mapping, enableGutterFill: true);

                float[] gpuPx = ReadSlice0((RenderTexture)gpu.Texture);
                Color[] cpuColors = ((Texture2DArray)cpu.Texture).GetPixels(0);

                // Compare the covered interior (skip gutter texels, where fill heuristics can differ).
                int res = ((RenderTexture)gpu.Texture).width;
                double sumAbs = 0; int n = 0;
                for (int y = res / 4; y < res * 3 / 4; y++)
                for (int x = res / 4; x < res * 3 / 4; x++)
                {
                    int i = y * res + x;
                    sumAbs += math.abs(gpuPx[i] - cpuColors[i].r);
                    n++;
                }
                double meanAbs = n > 0 ? sumAbs / n : 0;
                Assert.Less(meanAbs, 0.08, "GPU and CPU rasterizations should agree within tolerance in the interior");
            }
            finally { gpu?.Dispose(); cpu?.Dispose(); cWeights?.Dispose(); channelized.Dispose(); }
        }

        [Test]
        public void Gpu_GutterFillReducesEmptyTexels()
        {
            RequireGpu();
            using var source = TestMeshFactory.BuildPlane(6, 30f, Allocator.TempJob);
            using var weights = new WeightLayerSet(Allocator.TempJob);
            var grass = weights.InitializeLayer("Grass", source.VertexCount);
            for (int i = 0; i < source.VertexCount; i++) grass[i] = 1f;

            var channelized = ChannelUVUnwrap.Generate(source, weights, ChannelUVSettings.Default,
                Allocator.TempJob, out var cWeights, out var mapping);
            ChannelRasterResult filled = null, unfilled = null;
            try
            {
                filled = ChannelRasterizerGPU.Render(channelized, cWeights, mapping, enableGutterFill: true);
                if (filled == null) Assert.Ignore("GPU backend unavailable (resources not loaded).");
                unfilled = ChannelRasterizerGPU.Render(channelized, cWeights, mapping, enableGutterFill: false);

                int zerosFilled = CountZeros(ReadSlice0((RenderTexture)filled.Texture));
                int zerosUnfilled = CountZeros(ReadSlice0((RenderTexture)unfilled.Texture));
                Assert.LessOrEqual(zerosFilled, zerosUnfilled,
                    "gutter fill should not increase the number of empty texels");
            }
            finally { filled?.Dispose(); unfilled?.Dispose(); cWeights?.Dispose(); channelized.Dispose(); }
        }

        static int CountZeros(float[] px)
        {
            int n = 0;
            foreach (var v in px) if (v <= 0.0001f) n++;
            return n;
        }
    }
}
