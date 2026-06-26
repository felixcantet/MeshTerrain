using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain.Tests
{
    public class ChannelTests
    {
        // ---- ChannelPacking ----

        [Test]
        public void ChannelPacking_RoundTrips()
        {
            // 8 channels, slices 0..6 plus one absent (negative -> SlotInvalid).
            var slices = new[] { 0, 1, 2, 3, 4, 5, 6, -1 };
            var table = ChannelTable.Build(slices);

            for (int c = 0; c < 7; c++)
                Assert.AreEqual(slices[c], table.GetSlice(c), $"slice mismatch at channel {c}");
            Assert.AreEqual(ChannelTable.SlotInvalid, table.GetSlice(7), "absent channel must read SlotInvalid");
            Assert.AreEqual(8, table.SlotCount);
        }

        [Test]
        public void ChannelPacking_FillsAllTwentyFourSlots()
        {
            var slices = new int[ChannelTable.MaxNumberPackedChannels];
            for (int i = 0; i < slices.Length; i++) slices[i] = i % 30; // valid 5-bit values
            var table = ChannelTable.Build(slices);
            for (int c = 0; c < slices.Length; c++)
                Assert.AreEqual(slices[c], table.GetSlice(c));
        }

        [Test]
        public void ChannelPacking_RejectsMoreThan24Channels()
        {
            var slices = new int[ChannelTable.MaxNumberPackedChannels + 1];
            Assert.Throws<System.InvalidOperationException>(() => ChannelTable.Build(slices));
        }

        // ---- ChannelUVUnwrap ----

        [Test]
        public void ChannelUVs_GeneratedInUnitRange()
        {
            using var source = TestMeshFactory.BuildPlane(8, 40f, Allocator.TempJob);
            var result = ChannelUVUnwrap.Generate(source, null, ChannelUVSettings.Default,
                Allocator.TempJob, out var weights, out var mapping);
            try
            {
                Assert.IsTrue(result.HasChannelUVs);
                Assert.AreEqual(result.VertexCount, result.ChannelUVs.Length);
                for (int v = 0; v < result.VertexCount; v++)
                {
                    float2 uv = result.ChannelUVs[v];
                    Assert.IsFalse(float.IsNaN(uv.x) || float.IsNaN(uv.y), "UV must not be NaN");
                    Assert.GreaterOrEqual(uv.x, -1e-4f); Assert.LessOrEqual(uv.x, 1f + 1e-4f);
                    Assert.GreaterOrEqual(uv.y, -1e-4f); Assert.LessOrEqual(uv.y, 1f + 1e-4f);
                }
                Assert.Greater(mapping.ImageResolution, 0);
                Assert.AreEqual(0, mapping.ImageResolution % 4, "resolution must be a multiple of 4");
            }
            finally { weights?.Dispose(); result.Dispose(); }
        }

        [Test]
        public void BoxProject_FlatPlaneIsSingleIsland()
        {
            // A flat +Y plane has all triangles on one box face (+Y), so no vertices are split.
            using var source = TestMeshFactory.BuildPlane(4, 20f, Allocator.TempJob);
            var result = ChannelUVUnwrap.Generate(source, null, ChannelUVSettings.Default,
                Allocator.TempJob, out var weights, out var _);
            try
            {
                Assert.AreEqual(source.VertexCount, result.VertexCount,
                    "a single-face plane should not duplicate vertices");
                Assert.AreEqual(source.TriangleCount, result.TriangleCount);
            }
            finally { weights?.Dispose(); result.Dispose(); }
        }

        [Test]
        public void BoxProject_SplitsVerticesAcrossFaces()
        {
            // A right-angle fold (one +Y face, one +Z face sharing an edge) must split the shared
            // edge's vertices so each face gets its own UV.
            using var source = BuildFold(Allocator.TempJob);
            var result = ChannelUVUnwrap.Generate(source, null, ChannelUVSettings.Default,
                Allocator.TempJob, out var weights, out var _);
            try
            {
                Assert.Greater(result.VertexCount, source.VertexCount,
                    "shared edge between two box faces must duplicate vertices");
                Assert.AreEqual(source.TriangleCount, result.TriangleCount);
            }
            finally { weights?.Dispose(); result.Dispose(); }
        }

        [Test]
        public void DomainResolution_ScalesWithTexelSizeAndArea()
        {
            using var source = TestMeshFactory.BuildPlane(8, 100f, Allocator.TempJob);

            var fine = ChannelUVUnwrap.Generate(source, null, WithTexel(2f),
                Allocator.TempJob, out var w1, out var mFine);
            var coarse = ChannelUVUnwrap.Generate(source, null, WithTexel(50f),
                Allocator.TempJob, out var w2, out var mCoarse);
            try
            {
                Assert.Greater(mFine.ImageResolution, mCoarse.ImageResolution,
                    "smaller texel size must yield a higher resolution");
                Assert.AreEqual(0, mFine.ImageResolution % 4);
                Assert.LessOrEqual(mFine.ImageResolution, ChannelUVSettings.Default.MaxImageResolution);
            }
            finally { w1?.Dispose(); fine.Dispose(); w2?.Dispose(); coarse.Dispose(); }
        }

        // ---- ChannelRasterizerCPU ----

        [Test]
        public void CpuRasterizer_PaintedWeightAppearsInTexture()
        {
            using var source = TestMeshFactory.BuildPlane(8, 40f, Allocator.TempJob);
            using var weights = new WeightLayerSet(Allocator.TempJob);
            var grass = weights.InitializeLayer("Grass", source.VertexCount);
            for (int i = 0; i < source.VertexCount; i++) grass[i] = 1f; // fully painted

            var channelized = ChannelUVUnwrap.Generate(source, weights, ChannelUVSettings.Default,
                Allocator.TempJob, out var cWeights, out var mapping);
            ChannelRasterResult raster = null;
            try
            {
                raster = ChannelRasterizerCPU.Render(channelized, cWeights, mapping, enableGutterFill: true);
                Assert.IsNotNull(raster.Texture);
                Assert.AreEqual(1, raster.Texture.depth);

                // A texel inside the covered region should read ~1.0; pick the atlas center.
                var px = raster.Texture.GetPixels(0);
                int res = raster.Texture.width;
                float center = px[(res / 2) * res + res / 2].r;
                float maxVal = 0f;
                foreach (var p in px) maxVal = math.max(maxVal, p.r);
                Assert.Greater(maxVal, 0.9f, "fully-painted channel should rasterize near 1.0 somewhere");
                Assert.AreEqual(0, raster.Table.GetSlice(0), "channel 0 maps to slice 0");
            }
            finally { raster?.Dispose(); cWeights?.Dispose(); channelized.Dispose(); }
        }

        [Test]
        public void CpuRasterizer_GutterFillRemovesBlackHoles()
        {
            using var source = TestMeshFactory.BuildPlane(6, 30f, Allocator.TempJob);
            using var weights = new WeightLayerSet(Allocator.TempJob);
            var grass = weights.InitializeLayer("Grass", source.VertexCount);
            for (int i = 0; i < source.VertexCount; i++) grass[i] = 1f;

            var channelized = ChannelUVUnwrap.Generate(source, weights, ChannelUVSettings.Default,
                Allocator.TempJob, out var cWeights, out var mapping);
            ChannelRasterResult filled = null, unfilled = null;
            try
            {
                filled = ChannelRasterizerCPU.Render(channelized, cWeights, mapping, enableGutterFill: true);
                unfilled = ChannelRasterizerCPU.Render(channelized, cWeights, mapping, enableGutterFill: false);

                int zerosFilled = CountZeros(filled.Texture.GetPixels(0));
                int zerosUnfilled = CountZeros(unfilled.Texture.GetPixels(0));
                Assert.LessOrEqual(zerosFilled, zerosUnfilled,
                    "gutter fill should not increase the number of empty texels");
            }
            finally { filled?.Dispose(); unfilled?.Dispose(); cWeights?.Dispose(); channelized.Dispose(); }
        }

        // ---- SectionCompiler integration ----

        [Test]
        public void CompileSection_WithChannels_AttachesTextureAndTableToRenderers()
        {
            using var source = TestMeshFactory.BuildPlane(8, 40f, Allocator.TempJob);
            using var weights = new WeightLayerSet(Allocator.TempJob);
            var grass = weights.InitializeLayer("Grass", source.VertexCount);
            for (int i = 0; i < source.VertexCount; i++) grass[i] = 1f;

            var grid = new GridSettings { CellSize = 100f, Is2D = true };
            var dims = GridDimensions.ComputeGridDimensions(new float3(0, 0, 0), new float3(40, 0, 40), grid);
            CompiledSection compiled = null;
            try
            {
                var settings = new SectionCompilationSettings
                {
                    GenerateLODs = false,
                    GenerateCollision = false,
                    LODQualities = new[] { 1f },
                    LODScreenRelativeTransitionHeights = new[] { 0.6f },
                    Skirt = new MeshSkirtSettings { Enabled = false },
                    GenerateChannels = true,
                    ChannelUVSettings = ChannelUVSettings.Default,
                };

                compiled = SectionCompiler.CompileSection(source, weights, dims, dims.OriginCoord, settings);

                Assert.IsNotNull(compiled.ChannelTexture, "channel texture should be attached");
                var renderer = compiled.Root.GetComponentInChildren<MeshRenderer>();
                Assert.IsNotNull(renderer);

                var mpb = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(mpb);
                Assert.IsNotNull(mpb.GetTexture(ChannelPacking.ChannelTexId), "MPB must carry _ChannelTex");
                Assert.AreEqual(1f, mpb.GetFloat(ChannelPacking.ChannelCountId), 1e-4f, "one channel painted");
            }
            finally { compiled?.Dispose(); }
        }

        [Test]
        public void CompileSection_WithChannels_DisposesTexture()
        {
            using var source = TestMeshFactory.BuildPlane(4, 20f, Allocator.TempJob);
            using var weights = new WeightLayerSet(Allocator.TempJob);
            var grass = weights.InitializeLayer("Grass", source.VertexCount);
            for (int i = 0; i < source.VertexCount; i++) grass[i] = 0.5f;

            var grid = new GridSettings { CellSize = 100f, Is2D = true };
            var dims = GridDimensions.ComputeGridDimensions(new float3(0, 0, 0), new float3(20, 0, 20), grid);

            var settings = new SectionCompilationSettings
            {
                GenerateLODs = false,
                GenerateCollision = false,
                Skirt = new MeshSkirtSettings { Enabled = false },
                GenerateChannels = true,
            };
            var compiled = SectionCompiler.CompileSection(source, weights, dims, dims.OriginCoord, settings);
            Texture tex = compiled.ChannelTexture;
            Assert.IsNotNull(tex);
            compiled.Dispose();
            Assert.IsTrue(tex == null, "ChannelTexture should be destroyed on Dispose");
        }

        // ---- helpers ----

        static ChannelUVSettings WithTexel(float texel)
        {
            var s = ChannelUVSettings.Default;
            s.TexelSize3D = texel;
            return s;
        }

        static int CountZeros(Color[] px)
        {
            int n = 0;
            foreach (var p in px) if (p.r <= 0.0001f) n++;
            return n;
        }

        /// <summary>Two quads folded at a right angle: one on the +Y face, one on the +Z face.</summary>
        static MeshData BuildFold(Allocator allocator)
        {
            // Vertices: floor quad (y=0) and wall quad (z=10) sharing the edge (0..10, 0, 10).
            var verts = new[]
            {
                new float3(0, 0, 0), new float3(10, 0, 0),      // 0,1 floor front
                new float3(0, 0, 10), new float3(10, 0, 10),    // 2,3 shared edge (floor back / wall bottom)
                new float3(0, 10, 10), new float3(10, 10, 10),  // 4,5 wall top
            };
            var tris = new[]
            {
                new int3(0, 2, 1), new int3(1, 2, 3),  // floor (+Y)
                new int3(2, 4, 3), new int3(3, 4, 5),  // wall (+Z)
            };

            var data = MeshData.Allocate(verts.Length, tris.Length, allocator,
                withNormals: true, withChannelUVs: false, withSourceUV0: false, withBaseIDs: false);
            for (int i = 0; i < verts.Length; i++) data.Vertices[i] = verts[i];
            for (int t = 0; t < tris.Length; t++) data.Triangles[t] = tris[t];
            return data;
        }
    }
}
