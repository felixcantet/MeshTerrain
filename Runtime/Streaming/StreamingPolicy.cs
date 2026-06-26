using System.Collections.Generic;
using Unity.Mathematics;

namespace Fca.MeshTerrain.Streaming
{
    /// <summary>
    /// Pure streaming policy (<c>doc/08_STREAMING_SYSTEM_DESIGN.md §5</c>): maps a world-space focus to the
    /// set of cell coordinates that should be resident, with load/unload hysteresis. No scene/GPU state, so
    /// it is fully unit-testable. In <c>Is2D</c> mode the ring is 2D over X/Z and every coord has
    /// <c>y == 0</c>.
    /// </summary>
    public struct StreamingPolicy
    {
        public float3 Anchor;        // GridSettings.WorldOriginOffset
        public float CellSize;
        public bool Is2D;
        public int LoadRadiusCells;
        public int UnloadRadiusCells;
        /// <summary>True = Chebyshev (square ring); false = Euclidean (disc).</summary>
        public bool UseChebyshev;

        public static StreamingPolicy FromDistances(in GridSettings grid, float loadDistance, float unloadDistance, bool useChebyshev = true)
        {
            float cell = math.max(1e-3f, grid.CellSize);
            int load = math.max(0, (int)math.ceil(loadDistance / cell));
            int unload = math.max(load, (int)math.ceil(unloadDistance / cell));
            return new StreamingPolicy
            {
                Anchor = grid.WorldOriginOffset,
                CellSize = cell,
                Is2D = grid.Is2D,
                LoadRadiusCells = load,
                UnloadRadiusCells = unload,
                UseChebyshev = useChebyshev,
            };
        }

        /// <summary>The cell coordinate containing <paramref name="focusWorld"/> (same anchored floor snap as
        /// <see cref="GridDimensions.ComputeGridDimensions"/>).</summary>
        public int3 FocusCell(float3 focusWorld)
        {
            float3 rel = (focusWorld - Anchor) / CellSize;
            int3 c = (int3)math.floor(rel);
            if (Is2D) c.y = 0;
            return c;
        }

        /// <summary>Ring distance between two cell coords on X/Z (Y included only in 3D).</summary>
        public float RingDistance(int3 a, int3 b)
        {
            int dx = a.x - b.x;
            int dz = a.z - b.z;
            int dy = Is2D ? 0 : a.y - b.y;
            if (UseChebyshev)
                return math.max(math.abs(dx), math.max(math.abs(dy), math.abs(dz)));
            return math.sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Fills <paramref name="desired"/> with the coords within <see cref="LoadRadiusCells"/> of
        /// the focus cell.</summary>
        public void ComputeDesired(float3 focusWorld, HashSet<int3> desired)
        {
            desired.Clear();
            int3 center = FocusCell(focusWorld);
            int r = LoadRadiusCells;

            int yMin = Is2D ? 0 : -r;
            int yMax = Is2D ? 0 : r;
            for (int dz = -r; dz <= r; dz++)
                for (int dy = yMin; dy <= yMax; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int3 c = center + new int3(dx, dy, dz);
                        if (RingDistance(c, center) <= r)
                            desired.Add(c);
                    }
        }

        /// <summary>True if <paramref name="coord"/> is still within the unload (keep) radius of the focus —
        /// i.e. it must NOT be unloaded yet. The band between load and unload radii is the hysteresis.</summary>
        public bool ShouldKeep(int3 coord, float3 focusWorld)
            => RingDistance(coord, FocusCell(focusWorld)) <= UnloadRadiusCells;
    }
}
