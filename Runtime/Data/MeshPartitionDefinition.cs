using System.Collections.Generic;
using UnityEngine;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Shared configuration for a partitioned mega-mesh. Unity port of UE
    /// <c>UMeshPartitionDefinition</c> (see <c>doc/source/.../MeshPartitionDefinition.h</c>),
    /// trimmed to the fields the Unity roadmap needs at this stage. Later phases extend it:
    /// UV layout method → Phase 4, physical-material channels → Phase 3, build hashing → Phase 5.
    /// </summary>
    [CreateAssetMenu(menuName = "Mesh Terrain/Partition Definition", fileName = "MeshPartitionDefinition")]
    public sealed class MeshPartitionDefinition : ScriptableObject
    {
        /// <summary>Surface material. UE <c>UMeshPartitionDefinition.Material</c>.</summary>
        public Material Material;

        /// <summary>
        /// Channel (weight-layer) names declared for this mega-mesh.
        /// UE <c>FChannelMap.ChannelDescs[].Name</c>.
        /// </summary>
        public List<string> ChannelNames = new();

        /// <summary>
        /// Partition grid cell size in world units. 0 = no split (single section).
        /// Consumed by the grid in Phase 1 (UE <c>FGridSettings.CellSize</c>).
        /// </summary>
        public uint CellSize = 6400;

        /// <summary>Terrain mode: collapse the grid to a single Z column. UE <c>FGridSettings.bIs2D</c>.</summary>
        public bool Is2D = true;

        /// <summary>
        /// Triangle-count threshold above which a section is recursively sub-split (Phase 1).
        /// UE <c>FCommonBuildVariant.MaxSectionComplexity</c> (default 256*256*4 ≈ 262144).
        /// </summary>
        public float MaxSectionComplexity = 256f * 256f * 4f;

        /// <summary>
        /// Channel texture resolution: width of an ideal texel in world units (default 100 = 1 texel/m).
        /// Consumed by the channel rasterizer in Phase 4. UE <c>UMeshPartitionDefinition.ChannelTexelSize</c>.
        /// </summary>
        public float ChannelTexelSize = 100f;
    }
}
