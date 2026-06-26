using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Fca.MeshTerrain.Streaming;

namespace Fca.MeshTerrain.Tests
{
    /// <summary>
    /// EditMode tests for Phase 5.1: <see cref="SectionKey"/> hashing/stability, the blittable
    /// <see cref="SectionBlob"/> round-trip, the <see cref="SectionCooker"/>, and the two-tier
    /// <see cref="SectionCache"/> (a hit must avoid the cook). See
    /// <c>doc/08_STREAMING_SYSTEM_DESIGN.md §7, §14</c>.
    /// </summary>
    public class StreamingCacheTests
    {
        static List<ModifierComponent> Stack(params ModifierComponent[] mods) => new List<ModifierComponent>(mods);

        const float Eps = 1e-3f;

        static (GridSettings grid, GridDimensions dims) MakeGrid(List<ModifierComponent> stack, float cellSize)
        {
            var grid = new GridSettings { CellSize = cellSize, Is2D = true };
            var full = ModifierGroup.Process(stack, float4x4.identity, Allocator.TempJob);
            try
            {
                var p = MeshPartitioner.Partition(full.Mesh, grid, full.Weights, Allocator.TempJob);
                var dims = p.Dims;
                p.Dispose();
                return (grid, dims);
            }
            finally { full.Dispose(); }
        }

        static List<ModifierComponent> SampleStack() => Stack(
            new RectangleBaseModifier { Resolution = new int2(20, 20), Size = new float2(200, 200) },
            new WeightUtilityModifier { WeightChannelName = "Grass", Radius = 60, Falloff = 30, InnerValue = 1, OuterValue = 0 });

        // ---- SectionKey ----

        [Test]
        public void SectionKey_StableForUnchangedInputs()
        {
            var stack = SampleStack();
            var (grid, dims) = MakeGrid(stack, 100f);
            var channels = ChannelCookOptions.Default;
            int3 coord = dims.OriginCoord;

            var a = SectionKeyBuilder.Build(stack, grid, dims, coord, 10f, channels);
            var b = SectionKeyBuilder.Build(stack, grid, dims, coord, 10f, channels);

            Assert.AreEqual(a, b, "Same inputs must yield an equal key.");
            Assert.AreEqual(a.FileStem, b.FileStem, "Same inputs must yield the same disk filename.");
        }

        [Test]
        public void SectionKey_ChangesWhenCoveringModifierEdited_ScopedToItsCells()
        {
            var rect = new RectangleBaseModifier { Resolution = new int2(20, 20), Size = new float2(200, 200) };
            // Paint localized near one corner so it only covers some cells.
            var paint = new WeightUtilityModifier
            {
                Center = new float3(-80, 0, -80), WeightChannelName = "Grass",
                Radius = 20, Falloff = 10, InnerValue = 1, OuterValue = 0,
            };
            var stack = Stack(rect, paint);
            var (grid, dims) = MakeGrid(stack, 100f);
            var channels = ChannelCookOptions.Default;

            // A cell the paint covers, and a cell far from it.
            int3 near = dims.OriginCoord;                          // around (-100,-100) corner
            int3 far = dims.OriginCoord + new int3(dims.CellNumber.x - 1, 0, dims.CellNumber.z - 1);

            var nearBefore = SectionKeyBuilder.Build(stack, grid, dims, near, 10f, channels);
            var farBefore = SectionKeyBuilder.Build(stack, grid, dims, far, 10f, channels);

            paint.InnerValue = 0.5f; // edit the paint

            var nearAfter = SectionKeyBuilder.Build(stack, grid, dims, near, 10f, channels);
            var farAfter = SectionKeyBuilder.Build(stack, grid, dims, far, 10f, channels);

            Assert.AreNotEqual(nearBefore.ModifiersHash, nearAfter.ModifiersHash,
                "Editing a covering modifier must change the covered cell's hash.");
            Assert.AreEqual(farBefore.ModifiersHash, farAfter.ModifiersHash,
                "A cell the modifier does not cover must keep its hash (scoped invalidation).");
        }

        // ---- Blob round-trip ----

        [Test]
        public void SectionBlob_RoundTrips_GeometryAndWeights()
        {
            var stack = SampleStack();
            var (grid, dims) = MakeGrid(stack, 100f);
            int3 coord = FirstNonEmptyCoord(stack, grid, dims);

            var cooked = SectionCooker.Cook(stack, grid, dims, coord, 10f, ChannelCookOptions.Default, float4x4.identity, Allocator.TempJob);
            CookedSection restored = null;
            try
            {
                using var ms = new MemoryStream();
                SectionBlob.Write(ms, cooked);
                ms.Position = 0;
                Assert.IsTrue(SectionBlob.TryRead(ms, Allocator.TempJob, out restored), "Blob must read back.");

                Assert.AreEqual(cooked.Coord, restored.Coord);
                Assert.AreEqual(cooked.Key, restored.Key, "Key must survive the round-trip.");
                Assert.AreEqual(cooked.Mesh.VertexCount, restored.Mesh.VertexCount);
                Assert.AreEqual(cooked.Mesh.TriangleCount, restored.Mesh.TriangleCount);

                for (int v = 0; v < cooked.Mesh.VertexCount; v++)
                    Assert.IsTrue(math.all(math.abs(cooked.Mesh.Vertices[v] - restored.Mesh.Vertices[v]) < Eps),
                        $"Vertex {v} position must round-trip.");
                for (int t = 0; t < cooked.Mesh.TriangleCount; t++)
                    Assert.AreEqual(cooked.Mesh.Triangles[t], restored.Mesh.Triangles[t]);

                if (cooked.Weights != null && cooked.Weights.LayerCount > 0)
                {
                    Assert.IsNotNull(restored.Weights);
                    foreach (var name in cooked.Weights.LayerNames)
                    {
                        Assert.IsTrue(restored.Weights.HasLayer(name));
                        cooked.Weights.TryGetLayer(name, out var exp);
                        restored.Weights.TryGetLayer(name, out var act);
                        for (int i = 0; i < exp.Length; i++)
                            Assert.AreEqual(exp[i], act[i], Eps, $"Weight '{name}'[{i}] must round-trip.");
                    }
                }
            }
            finally { cooked.Dispose(); restored?.Dispose(); }
        }

        [Test]
        public void SectionBlob_RoundTrips_ChannelAtlas()
        {
            var stack = SampleStack();
            var (grid, dims) = MakeGrid(stack, 100f);
            int3 coord = FirstNonEmptyCoord(stack, grid, dims);
            var channels = new ChannelCookOptions { Generate = true, TexelSize3D = 100f, GutterFill = true };

            var cooked = SectionCooker.Cook(stack, grid, dims, coord, 10f, channels, float4x4.identity, Allocator.TempJob);
            CookedSection restored = null;
            try
            {
                Assert.IsTrue(cooked.HasAtlas, "Channel cook must produce an atlas blob.");

                using var ms = new MemoryStream();
                SectionBlob.Write(ms, cooked);
                ms.Position = 0;
                Assert.IsTrue(SectionBlob.TryRead(ms, Allocator.TempJob, out restored));

                Assert.AreEqual(cooked.ChannelAtlasResolution, restored.ChannelAtlasResolution);
                Assert.AreEqual(cooked.ChannelAtlasSlices, restored.ChannelAtlasSlices);
                Assert.AreEqual(cooked.ChannelTable.Words, restored.ChannelTable.Words, "Packing table words must round-trip.");
                Assert.AreEqual(cooked.ChannelTable.SlotCount, restored.ChannelTable.SlotCount);
                Assert.AreEqual(cooked.ChannelAtlasBlob.Length, restored.ChannelAtlasBlob.Length);
                for (int i = 0; i < cooked.ChannelAtlasBlob.Length; i++)
                    Assert.AreEqual(cooked.ChannelAtlasBlob[i], restored.ChannelAtlasBlob[i], $"Atlas byte {i} must round-trip.");
            }
            finally { cooked.Dispose(); restored?.Dispose(); }
        }

        // ---- Cache ----

        [Test]
        public void Cache_HitAvoidsCook()
        {
            var stack = SampleStack();
            var (grid, dims) = MakeGrid(stack, 100f);
            int3 coord = FirstNonEmptyCoord(stack, grid, dims);
            var channels = ChannelCookOptions.Default;

            string dir = Path.Combine(Path.GetTempPath(), "MeshTerrainTest_" + System.Guid.NewGuid().ToString("N"));
            var cache = new SectionCache("test", ramCapacity: 16, overrideDir: dir, allocator: Allocator.TempJob);
            try
            {
                var key = SectionKeyBuilder.Build(stack, grid, dims, coord, 10f, channels);

                int cooks = 0;
                CookedSection GetOrCook()
                {
                    if (cache.TryGet(key, out var hit)) return hit;
                    cooks++;
                    var cooked = SectionCooker.Cook(stack, grid, dims, coord, 10f, channels, float4x4.identity, Allocator.TempJob);
                    cache.Put(key, cooked);
                    return cooked;
                }

                var first = GetOrCook();
                Assert.AreEqual(1, cooks, "First access must cook once.");
                Assert.Greater(first.Mesh.VertexCount, 0, "Cooked section must carry geometry.");

                // Drop the RAM tier; the disk blob remains (first is now cache-disposed). A re-get must still
                // avoid cooking by reading the disk blob.
                cache.OnEvicted(coord);
                var second = GetOrCook();
                Assert.AreEqual(1, cooks, "A cache hit (disk) must not cook again.");
                Assert.Greater(second.Mesh.VertexCount, 0, "Reloaded section must carry geometry.");
            }
            finally
            {
                cache.Purge();
                cache.Dispose();
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Cache_EditedModifier_MissesAndRecooks()
        {
            var rect = new RectangleBaseModifier { Resolution = new int2(20, 20), Size = new float2(200, 200) };
            var paint = new WeightUtilityModifier { WeightChannelName = "Grass", Radius = 1000, Falloff = 1, InnerValue = 1, OuterValue = 1 };
            var stack = Stack(rect, paint);
            var (grid, dims) = MakeGrid(stack, 100f);
            var channels = ChannelCookOptions.Default;
            int3 coord = FirstNonEmptyCoord(stack, grid, dims);

            string dir = Path.Combine(Path.GetTempPath(), "MeshTerrainTest_" + System.Guid.NewGuid().ToString("N"));
            var cache = new SectionCache("test", 16, dir, Allocator.TempJob);
            try
            {
                int cooks = 0;
                void GetOrCook()
                {
                    var key = SectionKeyBuilder.Build(stack, grid, dims, coord, 10f, channels);
                    if (cache.TryGet(key, out _)) return;
                    cooks++;
                    var cooked = SectionCooker.Cook(stack, grid, dims, coord, 10f, channels, float4x4.identity, Allocator.TempJob);
                    cache.Put(key, cooked);
                }

                GetOrCook();
                Assert.AreEqual(1, cooks);

                paint.InnerValue = 0.25f; // edit → key changes → miss
                GetOrCook();
                Assert.AreEqual(2, cooks, "Editing a covering modifier must invalidate and recook.");
            }
            finally
            {
                cache.Purge();
                cache.Dispose();
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        // ---- helpers ----

        static int3 FirstNonEmptyCoord(List<ModifierComponent> stack, in GridSettings grid, in GridDimensions dims)
        {
            var full = ModifierGroup.Process(stack, float4x4.identity, Allocator.TempJob);
            try
            {
                var p = MeshPartitioner.Partition(full.Mesh, grid, full.Weights, Allocator.TempJob);
                try { return p.SectionCoords[0]; }
                finally { p.Dispose(); }
            }
            finally { full.Dispose(); }
        }
    }
}
