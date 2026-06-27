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
        /// <summary>When &gt; 0, bake the atlas at this fixed resolution (required by the shared-atlas
        /// instancing presenter so all section atlases fit one Texture2DArray). 0 = area-adaptive.</summary>
        public int FixedResolution;

        public static ChannelCookOptions Default => new ChannelCookOptions
        {
            Generate = false,
            TexelSize3D = 100f,
            GutterFill = true,
            FixedResolution = 0,
        };

        public static ChannelCookOptions FromDefinition(MeshPartitionDefinition def, bool generate)
            => new ChannelCookOptions
            {
                Generate = generate,
                TexelSize3D = def != null ? def.ChannelTexelSize : 100f,
                GutterFill = true,
                FixedResolution = 0,
            };
    }

    /// <summary>
    /// Options controlling LOD baking during the cook. When <see cref="BakeLods"/> is set, the skirt +
    /// simplified LOD chain is produced on the worker thread (instead of the main-thread present), so a
    /// cached tile reloads without any simplification (<c>doc/08 §8</c>). Folded into the variant hash.
    /// </summary>
    [System.Serializable]
    public struct LodCookOptions
    {
        public bool BakeLods;
        public float[] Qualities;          // e.g. { 1, 0.5, 0.25 }
        public MeshSkirtSettings Skirt;

        /// <summary>Generate the collision mesh during the cook (unskirted, simplified to
        /// <see cref="CollisionQuality"/>) so the present only uploads + bakes PhysX.</summary>
        public bool BakeCollision;
        /// <summary>Simplification quality of the baked collision mesh (1 = full-res, lower = cheaper PhysX).</summary>
        public float CollisionQuality;

        public static LodCookOptions Default => new LodCookOptions
        {
            BakeLods = true,
            Qualities = new[] { 1f, 0.5f, 0.25f },
            Skirt = MeshSkirtSettings.DefaultForCellSize(100f),
            BakeCollision = true,
            CollisionQuality = 0.25f,
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

        /// <summary>Cooked geometry (post-modifiers, post-partition for this one cell). When LODs are baked
        /// (<see cref="Lods"/> non-null) this is the unskirted/unsimplified mesh used for collision only.</summary>
        public MeshData Mesh;

        /// <summary>Per-vertex weight side-car (or null when the cook produced no weight layers).</summary>
        public WeightLayerSet Weights;

        /// <summary>Baked render LOD chain (skirt + simplified, weights packed), or null when LODs are baked at
        /// present time. LOD0 first. When set, the presenter is upload-only (no skirt/simplify on the main
        /// thread) — the fix for the LOD-simplify-in-present bottleneck (doc/08 §8).</summary>
        public LodMesh[] Lods;

        public bool HasBakedLods => Lods != null && Lods.Length > 0;

        /// <summary>Baked collision geometry — <b>unskirted</b> and (optionally) simplified on the worker, so
        /// the present only uploads + bakes PhysX (no skirt walls, cheaper cook than full-res). Null = use the
        /// main mesh. Has no weights/UVs (positions + indices only).</summary>
        public MeshData CollisionMesh;
        public bool HasBakedCollision => CollisionMesh.Vertices.IsCreated && CollisionMesh.TriangleCount > 0;

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

        /// <summary>Optional pre-serialized blob, produced on the cook's worker thread so the main-thread cache
        /// <c>Put</c> doesn't have to serialize (that serialization was a frame spike). Null for cache hits.</summary>
        public byte[] SerializedBlob;

        public bool HasAtlas => ChannelAtlasBlob != null && ChannelAtlasBlob.Length > 0;

        public void Dispose()
        {
            Mesh.Dispose();
            Weights?.Dispose();
            Weights = null;
            if (Lods != null)
            {
                foreach (var lod in Lods)
                {
                    lod.Mesh.Dispose();
                    lod.Weights?.Dispose();
                }
                Lods = null;
            }
            if (CollisionMesh.Vertices.IsCreated) CollisionMesh.Dispose();
            ChannelAtlasBlob = null;
        }
    }
}
