using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain.Streaming
{
    /// <summary>
    /// Produces the <b>cooked</b> data for one cell — the heavy half of the streaming pipeline
    /// (<c>doc/08_STREAMING_SYSTEM_DESIGN.md §2, §6</c>). Runs the bounded per-cell modifier build
    /// (<see cref="ModifierGroup.ProcessCell"/>) and, when requested, bakes the channel atlas into a
    /// portable R8 byte blob (so the result survives in the cache/on disk independent of the GPU).
    ///
    /// This is the part UE never does at runtime; in steady state it runs <b>once</b> per
    /// <c>(cell, params-hash)</c>, after which the cache serves prebaked bytes.
    /// </summary>
    public static class SectionCooker
    {
        /// <summary>
        /// Cooks the section at <paramref name="coord"/>. The atlas (if <c>channels.Generate</c>) is
        /// rasterized on the CPU into <see cref="CookedSection.ChannelAtlasBlob"/>, and the returned
        /// <see cref="CookedSection.Mesh"/> is the UV-seam-split mesh carrying the channel UVs. The caller
        /// owns and must dispose the returned <see cref="CookedSection"/>.
        /// </summary>
        public static CookedSection Cook(
            IReadOnlyList<ModifierComponent> stack,
            in GridSettings grid,
            in GridDimensions dims,
            int3 coord,
            float cellMargin,
            in ChannelCookOptions channels,
            float4x4 meshToWorld,
            Allocator allocator)
        {
            SectionKey key = SectionKeyBuilder.Build(stack, grid, dims, coord, cellMargin, channels);

            // Heavy: bounded modifier build + same-anchor partition extract for this one cell.
            ModifierResult cell = ModifierGroup.ProcessCell(stack, grid, dims, coord, cellMargin, meshToWorld, allocator);

            var cooked = new CookedSection
            {
                Coord = coord,
                Key = key,
                Mesh = cell.Mesh,
                Weights = cell.Weights,
                Dims = dims,
            };

            if (channels.Generate && cell.Mesh.TriangleCount > 0)
                BakeAtlas(cooked, channels, allocator);

            return cooked;
        }

        /// <summary>
        /// Generates channel UVs (replacing <see cref="CookedSection.Mesh"/> with the seam-split mesh) and
        /// rasterizes the weight layers into an R8 atlas, serialized into the blob. CPU backend so the cook
        /// is deterministic and GPU-free (the doc allows CPU raster for the cook; the live GPU texture is a
        /// presentation concern).
        /// </summary>
        static void BakeAtlas(CookedSection cooked, in ChannelCookOptions channels, Allocator allocator)
        {
            var uvSettings = ChannelUVSettings.Default;
            uvSettings.TexelSize3D = channels.TexelSize3D;

            // UV unwrap produces a fresh seam-split mesh + remapped weights; it becomes the cooked mesh so
            // the presenter renders exactly what was rasterized.
            MeshData split = ChannelUVUnwrap.Generate(
                cooked.Mesh, cooked.Weights, uvSettings, allocator,
                out WeightLayerSet splitWeights, out SectionDomainMapping mapping);

            ChannelRasterResult raster = ChannelRasterizerCPU.Render(
                split, splitWeights, mapping, channels.GutterFill);
            try
            {
                cooked.ChannelTable = raster.Table;
                cooked.ChannelTexcoordMetrics = raster.TexcoordMetrics;

                if (raster.Texture is Texture2DArray texArray)
                {
                    // Read the actual texture size (the rasterizer clamps resolution to >= 4).
                    cooked.ChannelAtlasResolution = texArray.width;
                    cooked.ChannelAtlasSlices = texArray.depth;
                    cooked.ChannelAtlasBlob = ExtractR8(texArray);
                }
                else
                {
                    // CPU backend always yields a Texture2DArray; defensive only.
                    cooked.ChannelAtlasResolution = 0;
                    cooked.ChannelAtlasSlices = 0;
                    cooked.ChannelAtlasBlob = null;
                }

                // Swap the cooked mesh/weights to the seam-split ones; dispose the originals.
                MeshData oldMesh = cooked.Mesh;
                WeightLayerSet oldWeights = cooked.Weights;
                cooked.Mesh = split;
                cooked.Weights = splitWeights;
                oldMesh.Dispose();
                oldWeights?.Dispose();
            }
            finally
            {
                raster.Dispose();
            }
        }

        /// <summary>Copies the R8 slices of a <see cref="Texture2DArray"/> into a tightly-packed byte blob
        /// (slice 0 first, then slice 1, …; each slice is <c>res*res</c> bytes).</summary>
        static byte[] ExtractR8(Texture2DArray texArray)
        {
            int res = texArray.width;
            int slices = texArray.depth;
            var blob = new byte[res * res * slices];
            int offset = 0;
            for (int s = 0; s < slices; s++)
            {
                // GetPixelData returns a NativeArray view into the texture's mip-0 slice.
                var slice = texArray.GetPixelData<byte>(0, s);
                NativeArray<byte>.Copy(slice, 0, blob, offset, slice.Length);
                offset += slice.Length;
            }
            return blob;
        }
    }
}
