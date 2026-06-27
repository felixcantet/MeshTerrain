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
            in LodCookOptions lodOptions,
            float4x4 meshToWorld,
            Allocator allocator)
        {
            SectionKey key = SectionKeyBuilder.Build(stack, grid, dims, coord, cellMargin, channels, lodOptions);

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

            // Bake the skirt + LOD chain on this worker thread so the present is upload-only (doc/08 §8).
            if (lodOptions.BakeLods && cooked.Mesh.TriangleCount > 0)
                BakeLods(cooked, lodOptions, allocator);

            // Serialize the blob here on the worker thread so the main-thread cache Put is a plain byte write
            // (serializing 3 LODs + collision + atlas was a frame spike otherwise).
            if (cooked.Mesh.TriangleCount > 0)
            {
                try
                {
                    using var ms = new System.IO.MemoryStream();
                    SectionBlob.Write(ms, cooked);
                    cooked.SerializedBlob = ms.ToArray();
                }
                catch (System.Exception e)
                {
                    UnityEngine.Debug.LogWarning($"SectionCooker: blob serialize failed for {coord} ({e.Message}).");
                }
            }

            return cooked;
        }

        /// <summary>Applies the skirt then bakes the simplified LOD chain (weights packed) on the worker
        /// thread. The cooked mesh is unchanged (kept for collision); the render LODs live in
        /// <see cref="CookedSection.Lods"/>.</summary>
        static void BakeLods(CookedSection cooked, in LodCookOptions lod, Allocator allocator)
        {
            MeshData renderBase = cooked.Mesh;
            WeightLayerSet renderWeights = cooked.Weights;
            MeshData skirted = default;
            WeightLayerSet skirtWeights = null;
            bool builtSkirt = false;

            if (lod.Skirt.Enabled)
            {
                skirted = SectionCompiler.BuildSkirt(cooked.Mesh, cooked.Weights, lod.Skirt, allocator, out skirtWeights);
                renderBase = skirted;
                renderWeights = skirtWeights;
                builtSkirt = true;
            }

            cooked.Lods = SectionLODBaker.Bake(renderBase, renderWeights, lod.Qualities, allocator);

            if (builtSkirt)
            {
                skirted.Dispose();
                skirtWeights?.Dispose();
            }

            // Collision: baked from the UNSKIRTED cooked mesh (no skirt walls in contact), simplified to a
            // cheaper quality so the present's PhysX cook is light. Geometry only (no weights).
            if (lod.BakeCollision)
            {
                float cq = lod.CollisionQuality <= 0f ? 0.25f : lod.CollisionQuality;
                if (cq >= 0.999f)
                {
                    // Full-res collision: copy the unskirted cooked mesh (positions/indices are what matter).
                    cooked.CollisionMesh = CopyGeometry(cooked.Mesh, allocator);
                }
                else
                {
                    var col = SectionLODBaker.SimplifyOnce(cooked.Mesh, null, cq, allocator);
                    cooked.CollisionMesh = col.Mesh;       // take ownership
                    col.Weights?.Dispose();                // none, but defensive
                }
            }
        }

        /// <summary>
        /// Builds a weight set containing EVERY global channel in <paramref name="globalNames"/> order: copies
        /// the section's values where present, fills zero where absent. This makes the rasterized atlas slice
        /// index equal the global channel index for every section (shared-atlas requirement).
        /// </summary>
        static WeightLayerSet NormalizeToGlobalChannels(WeightLayerSet src, string[] globalNames, int vertexCount, Allocator allocator)
        {
            var result = new WeightLayerSet(allocator);
            for (int i = 0; i < globalNames.Length; i++)
            {
                var dst = result.InitializeLayer(globalNames[i], vertexCount); // zero-filled by InitializeLayer
                if (src != null && src.TryGetLayer(globalNames[i], out var srcLayer))
                {
                    int n = math.min(vertexCount, srcLayer.Length);
                    for (int v = 0; v < n; v++) dst[v] = srcLayer[v];
                }
            }
            return result;
        }

        /// <summary>Positions + indices only copy (collision geometry needs no normals/UVs/weights).</summary>
        static MeshData CopyGeometry(in MeshData src, Allocator allocator)
        {
            var dst = MeshData.Allocate(src.VertexCount, src.TriangleCount, allocator,
                withNormals: false, withChannelUVs: false, withSourceUV0: false, withBaseIDs: false);
            for (int v = 0; v < src.VertexCount; v++) dst.Vertices[v] = src.Vertices[v];
            for (int t = 0; t < src.TriangleCount; t++) dst.Triangles[t] = src.Triangles[t];
            return dst;
        }

        /// <summary>
        /// Generates channel UVs (replacing <see cref="CookedSection.Mesh"/> with the seam-split mesh) and
        /// rasterizes the weight layers into an R8 atlas blob. Uses the Unity-object-free
        /// <see cref="ChannelRasterizerCPU.RenderToBytes"/> path so the whole cook is <b>thread-safe</b> and
        /// can run on a worker (<c>doc/08 §8</c>); the <see cref="Texture2DArray"/> is built later on the main
        /// thread by the presenter.
        /// </summary>
        static void BakeAtlas(CookedSection cooked, in ChannelCookOptions channels, Allocator allocator)
        {
            var uvSettings = ChannelUVSettings.Default;
            uvSettings.TexelSize3D = channels.TexelSize3D;
            uvSettings.FixedResolution = channels.FixedResolution; // 0 = adaptive; >0 = shared-atlas fixed size

            // Normalize the weight set to GLOBAL channel order so atlas slice i == global channel i for EVERY
            // section (the shared atlas requires this; otherwise a section's local layer order makes slice
            // indices inconsistent → channels scatter onto the wrong slices across tiles).
            WeightLayerSet normalized = null;
            WeightLayerSet weightsForRaster = cooked.Weights;
            if (channels.ChannelNames != null && channels.ChannelNames.Length > 0)
            {
                normalized = NormalizeToGlobalChannels(cooked.Weights, channels.ChannelNames, cooked.Mesh.VertexCount, allocator);
                weightsForRaster = normalized;
            }

            // UV unwrap produces a fresh seam-split mesh + remapped weights; it becomes the cooked mesh so
            // the presenter renders exactly what was rasterized.
            MeshData split = ChannelUVUnwrap.Generate(
                cooked.Mesh, weightsForRaster, uvSettings, allocator,
                out WeightLayerSet splitWeights, out SectionDomainMapping mapping);
            normalized?.Dispose();

            ChannelRasterBytes bytes = ChannelRasterizerCPU.RenderToBytes(
                split, splitWeights, mapping, channels.GutterFill);

            cooked.ChannelTable = bytes.Table;
            cooked.ChannelTexcoordMetrics = bytes.TexcoordMetrics;
            cooked.ChannelAtlasResolution = bytes.Resolution;
            cooked.ChannelAtlasSlices = bytes.Slices;
            cooked.ChannelAtlasBlob = bytes.R8;

            // Swap the cooked mesh/weights to the seam-split ones; dispose the originals.
            MeshData oldMesh = cooked.Mesh;
            WeightLayerSet oldWeights = cooked.Weights;
            cooked.Mesh = split;
            cooked.Weights = splitWeights;
            oldMesh.Dispose();
            oldWeights?.Dispose();
        }
    }
}
