using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain.Tests
{
    /// <summary>
    /// EditMode tests for the Phase 5.0 bounded per-cell build (<see cref="ModifierGroup.ProcessCell"/>).
    /// The central guard-rail (<c>doc/08_STREAMING_SYSTEM_DESIGN.md §6.3 / §14</c>) is the golden test:
    /// building a single cell in isolation must reproduce that cell extracted from a full build, triangle
    /// set for triangle set (ε on positions). Inputs use the Unity-native terrain convention (flat XZ
    /// plane, +Y up, <c>Is2D</c>).
    /// </summary>
    public class ProcessCellTests
    {
        static List<ModifierComponent> Stack(params ModifierComponent[] mods) => new List<ModifierComponent>(mods);

        const float Eps = 1e-3f;

        [Test]
        public void ProcessCell_FlatBase_MatchesFullBuild()
        {
            var rect = new RectangleBaseModifier { Resolution = new int2(20, 20), Size = new float2(200, 200) };
            AssertEveryCellMatchesFullBuild(Stack(rect), new GridSettings { CellSize = 100f, Is2D = true });
        }

        [Test]
        public void ProcessCell_WithNoiseAndPaint_MatchesFullBuild()
        {
            var rect = new RectangleBaseModifier { Resolution = new int2(24, 24), Size = new float2(240, 240) };
            var noise = new NoiseModifier
            {
                UnscaledCoverage = new float3(400, 400, 400), // tall enough to contain the displacement
                Intensity = 8.0, DisplacementType = NoiseType.SineWave,
                NoiseFrequency = new double2(8.0, 8.0), Falloff = 0.0,
            };
            var paint = new WeightUtilityModifier
            {
                WeightChannelName = "Grass", Radius = 60f, Falloff = 30f, InnerValue = 1f, OuterValue = 0f,
            };

            AssertEveryCellMatchesFullBuild(Stack(rect, noise, paint), new GridSettings { CellSize = 80f, Is2D = true });
        }

        [Test]
        public void ProcessCell_WithHeightFn_MatchesFullBuild()
        {
            var rect = new RectangleBaseModifier
            {
                Resolution = new int2(20, 20), Size = new float2(200, 200),
                HeightFn = uv => 15f * math.sin(uv.x * 6f) * math.cos(uv.y * 6f),
            };
            AssertEveryCellMatchesFullBuild(Stack(rect), new GridSettings { CellSize = 100f, Is2D = true });
        }

        [Test]
        public void ProcessCell_CellOutsideRectangle_IsEmpty()
        {
            var rect = new RectangleBaseModifier { Resolution = new int2(8, 8), Size = new float2(80, 80) };
            var grid = new GridSettings { CellSize = 40f, Is2D = true };

            // Build the full result once to learn the grid dims/anchor.
            var full = ModifierGroup.Process(Stack(rect), float4x4.identity, Allocator.TempJob);
            GridDimensions dims;
            try
            {
                var p = MeshPartitioner.Partition(full.Mesh, grid, full.Weights, Allocator.TempJob);
                dims = p.Dims;
                p.Dispose();
            }
            finally { full.Dispose(); }

            // A coordinate far outside the rectangle must produce no geometry.
            int3 farCoord = dims.OriginCoord + new int3(100, 0, 100);
            var cell = ModifierGroup.ProcessCell(Stack(rect), grid, dims, farCoord, CellMargin(grid), float4x4.identity, Allocator.TempJob);
            try
            {
                Assert.AreEqual(0, cell.Mesh.TriangleCount, "A cell outside the base rectangle must be empty.");
            }
            finally { cell.Dispose(); }
        }

        // ---- helpers ----

        static float CellMargin(in GridSettings grid) => grid.CellSize * 0.1f;

        /// <summary>
        /// Full build → partition; then for every non-empty section, build that same cell in isolation via
        /// <see cref="ModifierGroup.ProcessCell"/> and assert the two are the same mesh (triangle set + weights).
        /// </summary>
        static void AssertEveryCellMatchesFullBuild(List<ModifierComponent> stack, GridSettings grid)
        {
            var full = ModifierGroup.Process(stack, float4x4.identity, Allocator.TempJob);
            PartitionResult partition;
            try
            {
                partition = MeshPartitioner.Partition(full.Mesh, grid, full.Weights, Allocator.TempJob);
            }
            catch { full.Dispose(); throw; }

            try
            {
                Assert.Greater(partition.SectionCount, 1, "Test should split into multiple cells.");
                float margin = CellMargin(grid);

                for (int i = 0; i < partition.SectionCount; i++)
                {
                    int3 coord = partition.SectionCoords[i];
                    var cell = ModifierGroup.ProcessCell(stack, grid, partition.Dims, coord, margin, float4x4.identity, Allocator.TempJob);
                    try
                    {
                        AssertSameMesh(partition.Sections[i], cell.Mesh, coord);
                        AssertSameWeights(partition.SectionWeights, i, partition.Sections[i], cell, coord);
                    }
                    finally { cell.Dispose(); }
                }
            }
            finally { partition.Dispose(); full.Dispose(); }
        }

        /// <summary>Asserts both meshes describe the same triangle set (order-independent, ε on positions).</summary>
        static void AssertSameMesh(in MeshData expected, in MeshData actual, int3 coord)
        {
            Assert.AreEqual(expected.TriangleCount, actual.TriangleCount,
                $"Cell {coord}: triangle count mismatch (bounded vs full build).");

            var expectedTris = TriangleKeys(expected);
            var actualTris = TriangleKeys(actual);
            expectedTris.Sort();
            actualTris.Sort();

            for (int t = 0; t < expectedTris.Count; t++)
                Assert.AreEqual(expectedTris[t], actualTris[t],
                    $"Cell {coord}: triangle {t} differs between bounded and full build.");
        }

        /// <summary>Per-triangle canonical position key (rounded), rotation-normalised so winding-equivalent
        /// triangles compare equal regardless of which corner is listed first.</summary>
        static List<string> TriangleKeys(in MeshData mesh)
        {
            var keys = new List<string>(mesh.TriangleCount);
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                int3 tri = mesh.Triangles[t];
                string a = Q(mesh.Vertices[tri.x]);
                string b = Q(mesh.Vertices[tri.y]);
                string c = Q(mesh.Vertices[tri.z]);
                keys.Add(CanonicalRotation(a, b, c));
            }
            return keys;
        }

        static string CanonicalRotation(string a, string b, string c)
        {
            // Rotate so the lexicographically smallest vertex leads; preserves winding (no reflection).
            string r1 = a + "|" + b + "|" + c;
            string r2 = b + "|" + c + "|" + a;
            string r3 = c + "|" + a + "|" + b;
            string min = r1;
            if (string.CompareOrdinal(r2, min) < 0) min = r2;
            if (string.CompareOrdinal(r3, min) < 0) min = r3;
            return min;
        }

        static string Q(float3 p)
            => $"{math.round(p.x / Eps) * Eps:F3},{math.round(p.y / Eps) * Eps:F3},{math.round(p.z / Eps) * Eps:F3}";

        static void AssertSameWeights(WeightLayerSet[] fullWeights, int sectionIndex, in MeshData fullMesh,
            in ModifierResult cell, int3 coord)
        {
            if (fullWeights == null) return;
            var expected = fullWeights[sectionIndex];
            if (expected == null || expected.LayerCount == 0) return;

            // Build a position → weight map from the full-build section, then verify the bounded cell agrees
            // at the same positions (vertex ordering differs between the two builds).
            foreach (var name in expected.LayerNames)
            {
                Assert.IsTrue(cell.Weights.HasLayer(name), $"Cell {coord}: bounded build is missing channel '{name}'.");
                expected.TryGetLayer(name, out var expLayer);
                cell.Weights.TryGetLayer(name, out var actLayer);

                var byPos = new Dictionary<string, float>();
                for (int v = 0; v < fullMesh.VertexCount; v++)
                    byPos[Q(fullMesh.Vertices[v])] = expLayer[v];

                for (int v = 0; v < cell.Mesh.VertexCount; v++)
                {
                    string key = Q(cell.Mesh.Vertices[v]);
                    if (byPos.TryGetValue(key, out float expW))
                        Assert.AreEqual(expW, actLayer[v], 1e-3f,
                            $"Cell {coord}: channel '{name}' differs at {key}.");
                }
            }
        }
    }
}
