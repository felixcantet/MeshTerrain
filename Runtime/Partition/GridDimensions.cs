using Unity.Mathematics;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// Resolved grid layout for a given mesh AABB and <see cref="GridSettings"/>. Result of
    /// <see cref="ComputeGridDimensions"/>; Unity/Burst port of UE <c>ComputeGridDimensions</c>
    /// (see <c>doc/02_SYSTEM_ANALYSIS.md §4.1</c>, <c>MeshPartitionMeshBuilder.cpp</c>).
    ///
    /// The layout uses an <b>anchor-shifted floor snap</b>: cells align to multiples of
    /// <see cref="GridSettings.CellSize"/> from <see cref="GridSettings.WorldOriginOffset"/>, not from
    /// the mesh bounds. This makes <see cref="OriginCoord"/> (the absolute integer coordinate of the
    /// min cell) stable across edits — the key to a valid incremental cache later.
    /// </summary>
    public struct GridDimensions
    {
        /// <summary>Anchor-snapped minimum corner of the grid in local space.</summary>
        public float3 SnappedMin;

        /// <summary>Absolute integer coordinate of the minimum cell. Stable cache key per section.</summary>
        public int3 OriginCoord;

        /// <summary>Cell count along each axis. In <see cref="GridSettings.Is2D"/> mode, <c>y == 1</c>.</summary>
        public int3 CellNumber;

        /// <summary>
        /// Size of one cell. <c>(CellSize, CellSize, CellSize)</c>, except in 2D mode where the Y
        /// component spans the full Y extent of the mesh (single cell tall).
        /// </summary>
        public float3 CellExtent;

        /// <summary>Total number of cells in the grid (<c>CellNumber.x * y * z</c>).</summary>
        public int TotalCells => CellNumber.x * CellNumber.y * CellNumber.z;

        /// <summary>Converts a local cell coordinate (0..CellNumber-1) to a linear cell index.</summary>
        public int LinearIndex(int3 localCoord)
            => localCoord.x + localCoord.z * CellNumber.x + localCoord.y * CellNumber.x * CellNumber.z;

        /// <summary>Converts a linear cell index back to a local cell coordinate.</summary>
        public int3 LocalCoord(int linearIndex)
        {
            int plane = CellNumber.x * CellNumber.z;
            int y = linearIndex / plane;
            int rem = linearIndex - y * plane;
            int z = rem / CellNumber.x;
            int x = rem - z * CellNumber.x;
            return new int3(x, y, z);
        }

        /// <summary>Absolute cell coordinate (the section key) for a local cell coordinate.</summary>
        public int3 AbsoluteCoord(int3 localCoord) => OriginCoord + localCoord;

        /// <summary>Minimum corner (local space) of the given local cell.</summary>
        public float3 CellMin(int3 localCoord) => SnappedMin + (float3)localCoord * CellExtent;

        /// <summary>Center (local space) of the given local cell.</summary>
        public float3 CellCenter(int3 localCoord) => CellMin(localCoord) + CellExtent * 0.5f;

        /// <summary>
        /// Anchor-shifted floor snap. Given the mesh-local AABB and grid settings, computes the
        /// snapped origin, the absolute origin coordinate, the per-axis cell count and cell extent.
        /// See <c>doc/02_SYSTEM_ANALYSIS.md §4.1</c>.
        /// </summary>
        public static GridDimensions ComputeGridDimensions(float3 boundsMin, float3 boundsMax, in GridSettings grid)
        {
            float cell = grid.CellSize;
            float3 anchor = grid.WorldOriginOffset;

            // Snap the min corner down to the nearest anchored cell boundary.
            float3 cellsFromAnchor = math.floor((boundsMin - anchor) / cell);
            float3 snappedMin = cellsFromAnchor * cell + anchor;
            int3 originCoord = (int3)cellsFromAnchor;

            float3 extents = boundsMax - snappedMin;
            int3 cellNumber = math.max(1, (int3)math.ceil(extents / cell));
            float3 cellExtent = new float3(cell, cell, cell);

            // Unity-native flat axis is Y: collapse it to a single cell spanning the whole Y extent.
            if (grid.Is2D)
            {
                cellNumber.y = 1;
                originCoord.y = 0;
                cellExtent.y = boundsMax.y - snappedMin.y;
            }

            return new GridDimensions
            {
                SnappedMin = snappedMin,
                OriginCoord = originCoord,
                CellNumber = cellNumber,
                CellExtent = cellExtent,
            };
        }
    }
}
