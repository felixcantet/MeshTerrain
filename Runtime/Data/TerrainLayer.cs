using System;
using UnityEngine;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// One terrain material layer bound to a channel: the textures + params blended where that channel's
    /// weight is high. Channel <c>i</c> (by <see cref="MeshPartitionDefinition.ChannelNames"/>) uses layer
    /// <c>i</c> (by <see cref="MeshPartitionDefinition.ChannelLayers"/>). Mirrors the per-channel material
    /// data UE attaches to a Mesh Terrain material (albedo/normal/mask blended by channel weight).
    ///
    /// <para>Authoring layer (Phase: terrain material). The build step packs each layer's textures into the
    /// shared <c>Texture2DArray</c>s (slice = channel index) the terrain shader samples.</para>
    /// </summary>
    [Serializable]
    public struct TerrainLayer
    {
        [Tooltip("Base color (sRGB). Slice for this channel in the albedo array.")]
        public Texture2D Albedo;

        [Tooltip("Tangent-space normal map (linear). Slice for this channel in the normal array.")]
        public Texture2D Normal;

        [Tooltip("Packed mask: R=roughness, G=ambient occlusion, B=height (for height blend), A=metallic.")]
        public Texture2D Mask;

        [Tooltip("World-space tiling: layer UV = worldXZ / Tiling. Larger = bigger features.")]
        public float Tiling;

        [Tooltip("Normal map intensity.")]
        public float NormalStrength;

        [Tooltip("Height-blend contrast: how sharply this layer's height map biases the blend at transitions.")]
        public float HeightContrast;

        public static TerrainLayer Default => new TerrainLayer
        {
            Tiling = 10f,
            NormalStrength = 1f,
            HeightContrast = 0.5f,
        };
    }
}
