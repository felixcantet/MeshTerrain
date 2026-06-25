using Unity.Mathematics;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Configuration for the spatial partition grid. Unity/Burst port of UE
    /// <c>UE::MeshPartition::FGridSettings</c> (see
    /// <c>doc/source/.../MeshPartitionMeshBuilder.cpp</c>, <c>doc/02_SYSTEM_ANALYSIS.md §4.1</c>).
    ///
    /// Plain Burst-friendly struct (no managed members) so it can be captured directly by the
    /// partition jobs. Built from <see cref="MeshPartitionDefinition"/> at the call site.
    ///
    /// Axis convention (Unity-native): the grid tiles the X and Z axes; <see cref="Is2D"/> collapses
    /// the <b>Y</b> column to a single cell spanning the full Y extent of the mesh (terrain mode).
    /// This matches meshes laid out flat on XZ with +Y up. The UE original uses XY/Z-up; that
    /// difference is re-mapped when porting the Noise modifier in Phase 2.
    /// </summary>
    public struct GridSettings
    {
        /// <summary>Grid cell size in world units. UE <c>FGridSettings.CellSize</c>.</summary>
        public float CellSize;

        /// <summary>Terrain mode: collapse the grid to a single Y column. UE <c>FGridSettings.bIs2D</c>.</summary>
        public bool Is2D;

        /// <summary>
        /// Stable anchor origin for the grid (UE's World Partition origin expressed in local space).
        /// Cells are aligned to multiples of <see cref="CellSize"/> from this anchor, <b>not</b> from
        /// the mesh bounds — this keeps cell coordinates stable across edits so the incremental cache
        /// (Phase 5) stays valid. UE <c>FGridSettings.WorldOriginOffset</c>. Default <c>float3.zero</c>.
        /// </summary>
        public float3 WorldOriginOffset;

        /// <summary>Builds grid settings from a <see cref="MeshPartitionDefinition"/>.</summary>
        public static GridSettings FromDefinition(MeshPartitionDefinition definition, float3 worldOriginOffset = default)
            => new GridSettings
            {
                CellSize = definition.CellSize,
                Is2D = definition.Is2D,
                WorldOriginOffset = worldOriginOffset,
            };
    }
}
