using System;
using Unity.Mathematics;

namespace Fca.MeshTerrain.Streaming
{
    /// <summary>
    /// Options controlling whether/how the channel atlas is baked into a <see cref="CookedSection"/>.
    /// Folded into <see cref="SectionKey.VariantHash"/> so changing them invalidates the cache.
    /// </summary>
    [Serializable]
    public struct ChannelCookOptions
    {
        /// <summary>Generate channel UVs + rasterize the atlas during the cook.</summary>
        public bool Generate;
        /// <summary>World size (uu) of one ideal texel (Definition <c>ChannelTexelSize</c>).</summary>
        public float TexelSize3D;
        /// <summary>Run the pull-push gutter fill (the border fill always runs).</summary>
        public bool GutterFill;

        public static ChannelCookOptions Default => new ChannelCookOptions
        {
            Generate = false,
            TexelSize3D = 100f,
            GutterFill = true,
        };

        public static ChannelCookOptions FromDefinition(MeshPartitionDefinition def, bool generate)
            => new ChannelCookOptions
            {
                Generate = generate,
                TexelSize3D = def != null ? def.ChannelTexelSize : 100f,
                GutterFill = true,
            };
    }

    /// <summary>
    /// One section's <b>cooked</b> data, ready to present. Backend-agnostic — the streaming core only ever
    /// holds this (and a presenter interface), so an ECS presenter can be added later without touching the
    /// core (<c>doc/08_STREAMING_SYSTEM_DESIGN.md §4</c>).
    ///
    /// Owns native memory (<see cref="Mesh"/> + <see cref="Weights"/>): the holder <b>must</b>
    /// <see cref="Dispose"/>. The channel atlas is carried as a serialized byte blob (not a live texture)
    /// so a <see cref="CookedSection"/> survives in the cache/on disk independent of the GPU; the presenter
    /// uploads it when it instantiates.
    /// </summary>
    public sealed class CookedSection : IDisposable
    {
        public int3 Coord;
        public SectionKey Key;

        /// <summary>Cooked geometry (post-modifiers, post-partition for this one cell).</summary>
        public MeshData Mesh;

        /// <summary>Per-vertex weight side-car (or null when the cook produced no weight layers).</summary>
        public WeightLayerSet Weights;

        /// <summary>Serialized channel atlas (R8 slices), or null when channels were not generated.</summary>
        public byte[] ChannelAtlasBlob;

        /// <summary>Atlas dimensions for <see cref="ChannelAtlasBlob"/> (width == height, R8).</summary>
        public int ChannelAtlasResolution;
        public int ChannelAtlasSlices;

        /// <summary>Packing table mapping global channel → atlas slice.</summary>
        public ChannelTable ChannelTable;

        /// <summary>Per-section texcoord metric (UV→world size) the material needs.</summary>
        public float2 ChannelTexcoordMetrics;

        /// <summary>Grid layout this section belongs to — positions the presented root.</summary>
        public GridDimensions Dims;

        public bool HasAtlas => ChannelAtlasBlob != null && ChannelAtlasBlob.Length > 0;

        public void Dispose()
        {
            Mesh.Dispose();
            Weights?.Dispose();
            Weights = null;
            ChannelAtlasBlob = null;
        }
    }
}
