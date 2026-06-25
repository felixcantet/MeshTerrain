using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace Fca.MeshTerrain.Tests
{
    /// <summary>
    /// EditMode tests for the Phase 2 non-destructive modifier stack: MeshView, ProcessModifierGroup, and
    /// the Rectangle base / Noise / WeightUtility modifiers. Inputs use the Unity-native axis convention
    /// (flat XZ plane, +Y up).
    /// </summary>
    public class ModifierTests
    {
        static List<ModifierComponent> Stack(params ModifierComponent[] mods) => new List<ModifierComponent>(mods);

        // ---- RectangleBaseModifier ----

        [Test]
        public void RectangleBase_ProducesExpectedTopologyAndAttributes()
        {
            var rect = new RectangleBaseModifier { Resolution = new int2(4, 3), Size = new float2(40, 30) };
            var mesh = rect.ProduceBaseMesh(Allocator.Temp);
            try
            {
                Assert.AreEqual(5 * 4, mesh.VertexCount);     // (x+1)*(z+1)
                Assert.AreEqual(4 * 3 * 2, mesh.TriangleCount); // x*z*2
                Assert.IsTrue(mesh.HasNormals);
                Assert.IsTrue(mesh.HasSourceUV0);
                Assert.IsTrue(mesh.HasBaseIDs);
                for (int v = 0; v < mesh.VertexCount; v++)
                    Assert.AreEqual(new float3(0, 1, 0), mesh.Normals[v]);
            }
            finally { mesh.Dispose(); }
        }

        // ---- MeshView ----

        [Test]
        public void MeshView_CollectsOnlyInBoundsVertices_AndWritesBack()
        {
            var rect = new RectangleBaseModifier { Resolution = new int2(4, 4), Size = new float2(40, 40) };
            var mesh = rect.ProduceBaseMesh(Allocator.Temp);
            using var weights = new WeightLayerSet(Allocator.Temp);
            try
            {
                // Bounds covering only one corner quad (around local (-20,-20)).
                var bounds = new Bounds(new Vector3(-15, 0, -15), new Vector3(15, 10, 15));
                var view = new MeshView(mesh, weights, bounds,
                    MeshViewComponents.VertexPos, MeshViewComponents.VertexPos, null);
                view.Build();

                Assert.Greater(view.VertexCount, 0);
                Assert.Less(view.VertexCount, mesh.VertexCount, "View must be a strict subset.");

                // Shift every collected vertex up by 5 (stays within the Y bounds) and write back.
                for (int i = 0; i < view.VertexCount; i++)
                {
                    float3 p = view.GetVertexPos(i);
                    view.SetVertexPos(i, new float3(p.x, p.y + 5f, p.z));
                }
                view.Writeback();

                int raised = 0;
                for (int v = 0; v < mesh.VertexCount; v++)
                    if (mesh.Vertices[v].y > 4.9f) raised++;
                Assert.AreEqual(view.VertexCount, raised, "Exactly the in-bounds vertices were raised.");
            }
            finally { mesh.Dispose(); }
        }

        [Test]
        public void MeshView_SetVertexPosOutsideBounds_IsRejected()
        {
            var rect = new RectangleBaseModifier { Resolution = new int2(2, 2), Size = new float2(20, 20) };
            var mesh = rect.ProduceBaseMesh(Allocator.Temp);
            using var weights = new WeightLayerSet(Allocator.Temp);
            try
            {
                var bounds = new Bounds(Vector3.zero, new Vector3(40, 10, 40));
                var view = new MeshView(mesh, weights, bounds,
                    MeshViewComponents.VertexPos, MeshViewComponents.VertexPos, null);
                view.Build();
                Assert.Greater(view.VertexCount, 0);

                float3 before = view.GetVertexPos(0);
                LogAssert.ignoreFailingMessages = true; // the rejection logs a Debug.Assert
                view.SetVertexPos(0, new float3(1000, 1000, 1000)); // far outside bounds
                LogAssert.ignoreFailingMessages = false;

                Assert.AreEqual(before, view.GetVertexPos(0), "Out-of-bounds write must be rejected (no-op).");
            }
            finally { mesh.Dispose(); }
        }

        // ---- WeightUtilityModifier ----

        [Test]
        public void WeightUtility_PaintsInnerOuterWithFalloff()
        {
            var rect = new RectangleBaseModifier { Resolution = new int2(10, 10), Size = new float2(100, 100) };
            var paint = new WeightUtilityModifier
            {
                Center = float3.zero, WeightChannelName = "Grass",
                Radius = 20f, Falloff = 10f, InnerValue = 1f, OuterValue = 0f,
            };

            var result = ModifierGroup.Process(Stack(rect, paint), float4x4.identity, Allocator.Temp);
            try
            {
                Assert.IsTrue(result.Weights.HasLayer("Grass"));
                result.Weights.TryGetLayer("Grass", out var layer);
                var mesh = result.Mesh;

                for (int v = 0; v < mesh.VertexCount; v++)
                {
                    float3 p = mesh.Vertices[v];
                    float dist = math.distance(new float2(p.x, p.z), float2.zero);
                    if (dist <= 20f) Assert.AreEqual(1f, layer[v], 1e-4f, "Inside radius → inner value.");
                    else if (dist >= 30f) Assert.AreEqual(0f, layer[v], 1e-4f, "Beyond radius+falloff → outer value.");
                }
            }
            finally { result.Dispose(); }
        }

        // ---- NoiseModifier ----

        [Test]
        public void Noise_ZeroIntensity_IsNoOp()
        {
            var rect = new RectangleBaseModifier { Resolution = new int2(8, 8), Size = new float2(80, 80) };
            var noise = new NoiseModifier
            {
                UnscaledCoverage = new float3(100, 100, 100),
                Intensity = 0.0, DisplacementType = NoiseType.Fbm, Falloff = 0.0,
            };

            var result = ModifierGroup.Process(Stack(rect, noise), float4x4.identity, Allocator.Temp);
            try
            {
                foreach (var v in IterVerts(result.Mesh))
                    Assert.AreEqual(0f, v.y, 1e-4f, "Zero intensity must not displace.");
            }
            finally { result.Dispose(); }
        }

        [Test]
        public void Noise_DisplacesAlongWorldY_OnFlatXZPlane()
        {
            var rect = new RectangleBaseModifier { Resolution = new int2(16, 16), Size = new float2(160, 160) };
            var noise = new NoiseModifier
            {
                UnscaledCoverage = new float3(200, 200, 200), // tall enough to contain the displacement
                Intensity = 10.0, DisplacementType = NoiseType.SineWave,
                // PatchUV spans ~[-0.4,0.4]; a high frequency is needed to sweep the sine through a useful range.
                NoiseFrequency = new double2(15.0, 15.0), Falloff = 0.0,
            };

            var result = ModifierGroup.Process(Stack(rect, noise), float4x4.identity, Allocator.Temp);
            try
            {
                float maxAbsY = 0f;
                foreach (var v in IterVerts(result.Mesh))
                    maxAbsY = math.max(maxAbsY, math.abs(v.y));
                Assert.Greater(maxAbsY, 0.5f, "Sine noise must displace vertices along world Y.");
            }
            finally { result.Dispose(); }
        }

        [Test]
        public void Noise_WriteToWeightChannel_PopulatesLayer()
        {
            var rect = new RectangleBaseModifier { Resolution = new int2(8, 8), Size = new float2(80, 80) };
            var noise = new NoiseModifier
            {
                UnscaledCoverage = new float3(100, 100, 100),
                Intensity = 5.0, DisplacementType = NoiseType.SineWave,
                NoiseFrequency = new double2(15.0, 15.0), Falloff = 0.0,
                WriteToWeightChannel = true, WeightChannelName = "NoiseMask",
            };

            var result = ModifierGroup.Process(Stack(rect, noise), float4x4.identity, Allocator.Temp);
            try
            {
                Assert.IsTrue(result.Weights.HasLayer("NoiseMask"));
                result.Weights.TryGetLayer("NoiseMask", out var layer);
                bool anyNonZero = false;
                for (int i = 0; i < layer.Length; i++) if (math.abs(layer[i]) > 1e-5f) { anyNonZero = true; break; }
                Assert.IsTrue(anyNonZero, "Noise must write a non-trivial mask into the channel.");
            }
            finally { result.Dispose(); }
        }

        // ---- ProcessModifierGroup ordering & non-destructive recompile ----

        [Test]
        public void Process_AppliesModifiersInPriorityOrder()
        {
            var rect = new RectangleBaseModifier { Resolution = new int2(2, 2), Size = new float2(20, 20) };
            // Two paints on the same channel; the higher-priority one must win (applied last).
            var first = new WeightUtilityModifier { WeightChannelName = "C", Radius = 1000, Falloff = 1, InnerValue = 0.25f, OuterValue = 0.25f, PriorityLayer = 0 };
            var second = new WeightUtilityModifier { WeightChannelName = "C", Radius = 1000, Falloff = 1, InnerValue = 0.75f, OuterValue = 0.75f, PriorityLayer = 1 };

            // Pass the paints out of priority order to prove ordering isn't input-order-dependent.
            var result = ModifierGroup.Process(Stack(second, rect, first), float4x4.identity, Allocator.Temp);
            try
            {
                result.Weights.TryGetLayer("C", out var layer);
                for (int i = 0; i < layer.Length; i++)
                    Assert.AreEqual(0.75f, layer[i], 1e-4f, "Higher PriorityLayer must apply last and win.");
            }
            finally { result.Dispose(); }
        }

        [Test]
        public void Process_DisablingModifier_ReproducesLowerStack()
        {
            var rect = new RectangleBaseModifier { Resolution = new int2(12, 12), Size = new float2(120, 120) };
            var noise = new NoiseModifier
            {
                UnscaledCoverage = new float3(200, 200, 200),
                Intensity = 8.0, DisplacementType = NoiseType.SineWave,
                NoiseFrequency = new double2(12.0, 12.0), Falloff = 0.0,
            };
            var paint = new WeightUtilityModifier { WeightChannelName = "Rock", Radius = 30, Falloff = 10, InnerValue = 1, OuterValue = 0 };

            // With noise enabled, then disabled.
            var withNoise = ModifierGroup.Process(Stack(rect, noise, paint), float4x4.identity, Allocator.Temp);
            noise.IsDisabled = true;
            var withoutNoise = ModifierGroup.Process(Stack(rect, noise, paint), float4x4.identity, Allocator.Temp);
            try
            {
                var a = withNoise.Mesh; var b = withoutNoise.Mesh;
                Assert.AreEqual(a.VertexCount, b.VertexCount);

                bool anyDiff = false;
                for (int v = 0; v < b.VertexCount; v++)
                {
                    // Disabled-noise result must be the flat base (Y == 0 everywhere).
                    Assert.AreEqual(0f, b.Vertices[v].y, 1e-4f, "Disabled noise must leave the base undisplaced.");
                    if (math.abs(a.Vertices[v].y) > 1e-4f) anyDiff = true;
                }
                Assert.IsTrue(anyDiff, "Sanity: the enabled-noise run actually displaced something.");

                // The paint (lower-stack effect) is identical in both runs — non-destructive.
                withNoise.Weights.TryGetLayer("Rock", out var rockA);
                withoutNoise.Weights.TryGetLayer("Rock", out var rockB);
                for (int i = 0; i < rockB.Length; i++)
                    Assert.AreEqual(rockB[i], rockA[i], 1e-4f, "Channel paint must be unaffected by toggling noise.");
            }
            finally { withNoise.Dispose(); withoutNoise.Dispose(); }
        }

        // ---- End-to-end: stack → partition ----

        [Test]
        public void EndToEnd_StackThenPartition_YieldsSectionsWithChannel()
        {
            var rect = new RectangleBaseModifier { Resolution = new int2(20, 20), Size = new float2(200, 200) };
            var noise = new NoiseModifier
            {
                UnscaledCoverage = new float3(300, 200, 300),
                Intensity = 6.0, DisplacementType = NoiseType.SineWave,
                NoiseFrequency = new double2(0.1, 0.1), Falloff = 0.1,
            };
            var paint = new WeightUtilityModifier { WeightChannelName = "Grass", Radius = 50, Falloff = 20, InnerValue = 1, OuterValue = 0 };

            var result = ModifierGroup.Process(Stack(rect, noise, paint), float4x4.identity, Allocator.TempJob);
            try
            {
                var grid = new GridSettings { CellSize = 100f, Is2D = true };
                var partition = MeshPartitioner.Partition(result.Mesh, grid, result.Weights, Allocator.TempJob);
                try
                {
                    Assert.Greater(partition.SectionCount, 1, "A 200u mesh on a 100u grid should split into multiple sections.");

                    int sumTris = 0;
                    foreach (var sec in partition.Sections) sumTris += sec.TriangleCount;
                    Assert.AreEqual(result.Mesh.TriangleCount, sumTris, "No triangle lost or duplicated.");

                    Assert.IsNotNull(partition.SectionWeights);
                    foreach (var sw in partition.SectionWeights)
                        Assert.IsTrue(sw.HasLayer("Grass"), "Painted channel must survive the partition.");
                }
                finally { partition.Dispose(); }
            }
            finally { result.Dispose(); }
        }

        // ---- helpers ----

        static IEnumerable<float3> IterVerts(MeshData mesh)
        {
            for (int v = 0; v < mesh.VertexCount; v++) yield return mesh.Vertices[v];
        }
    }
}
