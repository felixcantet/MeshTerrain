using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Fca.MeshTerrain.Streaming;

namespace Fca.MeshTerrain.Tests
{
    /// <summary>
    /// EditMode tests for the pure streaming policy (<c>doc/08_STREAMING_SYSTEM_DESIGN.md §5, §14</c>):
    /// the desired ring set and the load/unload hysteresis band, with no scene/GPU state.
    /// </summary>
    public class StreamingPolicyTests
    {
        static StreamingPolicy Policy(float cell, int load, int unload, bool is2D = true, bool chebyshev = true)
            => new StreamingPolicy
            {
                Anchor = float3.zero, CellSize = cell, Is2D = is2D,
                LoadRadiusCells = load, UnloadRadiusCells = unload, UseChebyshev = chebyshev,
            };

        [Test]
        public void FocusCell_UsesAnchoredFloorSnap()
        {
            var p = Policy(100f, 1, 2);
            Assert.AreEqual(new int3(0, 0, 0), p.FocusCell(new float3(50, 0, 50)));
            Assert.AreEqual(new int3(1, 0, 1), p.FocusCell(new float3(150, 0, 150)));
            Assert.AreEqual(new int3(-1, 0, -1), p.FocusCell(new float3(-1, 0, -1)));
        }

        [Test]
        public void FocusCell_Is2D_CollapsesY()
        {
            var p = Policy(100f, 1, 2, is2D: true);
            Assert.AreEqual(0, p.FocusCell(new float3(50, 9999, 50)).y, "2D focus cell must have y == 0.");
        }

        [Test]
        public void ComputeDesired_ChebyshevRingCountMatchesRadius()
        {
            var p = Policy(100f, 2, 3, chebyshev: true);
            var desired = new HashSet<int3>();
            p.ComputeDesired(new float3(0, 0, 0), desired);

            // Chebyshev radius 2 over X/Z (2D) -> a 5x5 block = 25 cells.
            Assert.AreEqual(25, desired.Count);
            Assert.IsTrue(desired.Contains(new int3(0, 0, 0)));
            Assert.IsTrue(desired.Contains(new int3(2, 0, 2)));
            Assert.IsFalse(desired.Contains(new int3(3, 0, 0)), "Outside the load radius must be excluded.");
        }

        [Test]
        public void ComputeDesired_EuclideanIsADisc()
        {
            var p = Policy(100f, 2, 3, chebyshev: false);
            var desired = new HashSet<int3>();
            p.ComputeDesired(new float3(0, 0, 0), desired);

            // The corner (2,2) has distance sqrt(8) > 2 -> excluded from a disc but present in a square.
            Assert.IsFalse(desired.Contains(new int3(2, 0, 2)), "Euclidean disc must exclude far corners.");
            Assert.IsTrue(desired.Contains(new int3(2, 0, 0)));
            Assert.IsTrue(desired.Contains(new int3(1, 0, 1)));
        }

        [Test]
        public void Hysteresis_KeepsBetweenLoadAndUnloadRadius()
        {
            var p = Policy(100f, load: 2, unload: 4);
            float3 focus = float3.zero;

            var desired = new HashSet<int3>();
            p.ComputeDesired(focus, desired);

            // A cell at ring distance 3: NOT in the desired (load) set, but still kept (inside unload radius).
            int3 band = new int3(3, 0, 0);
            Assert.IsFalse(desired.Contains(band), "Ring-3 cell is outside the load radius.");
            Assert.IsTrue(p.ShouldKeep(band, focus), "Ring-3 cell is inside the unload radius (hysteresis band).");

            // A cell at ring distance 5: outside both -> must be unloaded.
            Assert.IsFalse(p.ShouldKeep(new int3(5, 0, 0), focus), "Ring-5 cell must be unloaded.");
        }

        [Test]
        public void FromDistances_DerivesRadiiAndClampsUnloadAboveLoad()
        {
            var grid = new GridSettings { CellSize = 100f, Is2D = true };
            var p = StreamingPolicy.FromDistances(grid, loadDistance: 250f, unloadDistance: 100f);
            Assert.AreEqual(3, p.LoadRadiusCells, "ceil(250/100) = 3.");
            Assert.GreaterOrEqual(p.UnloadRadiusCells, p.LoadRadiusCells, "Unload radius must never be below load radius.");
        }
    }
}
