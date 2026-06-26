using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Fca.MeshTerrain.Streaming;

namespace Fca.MeshTerrain.Tests
{
    /// <summary>
    /// EditMode tests for Phase 5.2: the <see cref="ISectionPresenter"/> seam. Presenting a
    /// <see cref="CookedSection"/> via <see cref="GameObjectSectionPresenter"/> must produce the same
    /// GameObject structure as a direct <see cref="SectionCompiler"/> compile, bind the prebaked channel
    /// atlas when present, and release everything (<c>doc/08_STREAMING_SYSTEM_DESIGN.md §4, §14</c>).
    /// </summary>
    public class PresenterTests
    {
        static List<ModifierComponent> SampleStack() => new List<ModifierComponent>
        {
            new RectangleBaseModifier { Resolution = new int2(20, 20), Size = new float2(200, 200) },
            new WeightUtilityModifier { WeightChannelName = "Grass", Radius = 60, Falloff = 30, InnerValue = 1, OuterValue = 0 },
        };

        static (GridSettings grid, GridDimensions dims, int3 coord) Setup(List<ModifierComponent> stack)
        {
            var grid = new GridSettings { CellSize = 100f, Is2D = true };
            var full = ModifierGroup.Process(stack, float4x4.identity, Allocator.TempJob);
            try
            {
                var p = MeshPartitioner.Partition(full.Mesh, grid, full.Weights, Allocator.TempJob);
                try { return (grid, p.Dims, p.SectionCoords[0]); }
                finally { p.Dispose(); }
            }
            finally { full.Dispose(); }
        }

        static SectionCompilationSettings Settings() => new SectionCompilationSettings
        {
            GenerateLODs = true,
            GenerateCollision = true,
            LODQualities = new[] { 1f, 0.5f, 0.25f },
            LODScreenRelativeTransitionHeights = new[] { 0.6f, 0.3f, 0.1f },
            Skirt = MeshSkirtSettings.DefaultForCellSize(100f),
        };

        [Test]
        public void Present_WithoutChannels_MatchesSectionCompilerStructure()
        {
            var stack = SampleStack();
            var (grid, dims, coord) = Setup(stack);
            var cooked = SectionCooker.Cook(stack, grid, dims, coord, 10f, ChannelCookOptions.Default, float4x4.identity, Allocator.TempJob);

            var root = new GameObject("PresenterTestRoot");
            var presenter = new GameObjectSectionPresenter(Settings());
            ISectionHandle handle = null;
            try
            {
                handle = presenter.Present(cooked, root.transform);
                Assert.IsNotNull(handle);
                Assert.AreEqual(coord, handle.Coord);

                // A section GO must have been created under root, with an LODGroup + 3 LOD renderers + collider.
                Assert.AreEqual(1, root.transform.childCount, "Presenter must parent one section GO under root.");
                var sectionGo = root.transform.GetChild(0).gameObject;
                Assert.IsNotNull(sectionGo.GetComponent<LODGroup>());
                Assert.AreEqual(3, sectionGo.GetComponentsInChildren<MeshRenderer>().Length);
                Assert.IsNotNull(sectionGo.GetComponent<MeshCollider>());

                // Root positioned at the section's grid-cell origin (same rule as SectionCompiler).
                float3 expectedOrigin = SectionCompiler.SectionOrigin(dims, coord);
                Assert.AreEqual((Vector3)expectedOrigin, sectionGo.transform.localPosition);
            }
            finally
            {
                if (handle != null) presenter.Release(handle);
                cooked.Dispose();
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Present_WithChannels_BindsAtlasAndReleasesCleanly()
        {
            var stack = SampleStack();
            var (grid, dims, coord) = Setup(stack);
            var channels = new ChannelCookOptions { Generate = true, TexelSize3D = 100f, GutterFill = true };
            var cooked = SectionCooker.Cook(stack, grid, dims, coord, 10f, channels, float4x4.identity, Allocator.TempJob);

            var root = new GameObject("PresenterTestRoot");
            var presenter = new GameObjectSectionPresenter(Settings());
            ISectionHandle handle = null;
            GameObject sectionGo = null;
            try
            {
                Assert.IsTrue(cooked.HasAtlas, "Channel cook must have produced an atlas.");

                handle = presenter.Present(cooked, root.transform);
                sectionGo = root.transform.GetChild(0).gameObject;

                // Atlas must be bound onto each renderer's property block.
                var mpb = new MaterialPropertyBlock();
                bool anyTexBound = false;
                foreach (var r in sectionGo.GetComponentsInChildren<MeshRenderer>())
                {
                    r.GetPropertyBlock(mpb);
                    if (mpb.GetTexture(ChannelPacking.ChannelTexId) != null) anyTexBound = true;
                }
                Assert.IsTrue(anyTexBound, "Prebaked atlas must be bound to the section renderers.");

                // Release must tear down the GO (and the atlas texture with it).
                presenter.Release(handle);
                handle = null;
                Assert.IsTrue(sectionGo == null, "Release must destroy the section GameObject.");
            }
            finally
            {
                if (handle != null) presenter.Release(handle);
                cooked.Dispose();
                Object.DestroyImmediate(root);
            }
        }
    }
}
