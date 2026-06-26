using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain.Streaming
{
    /// <summary>
    /// Default <see cref="ISectionPresenter"/> (Phase 5.2): presents a <see cref="CookedSection"/> as a
    /// <c>GameObject</c> hierarchy by delegating to the existing <see cref="SectionCompiler"/> (reusing its
    /// LOD/skirt/collision path unchanged), then binding the channel atlas.
    ///
    /// <para>The cook is the single source of the atlas: when the section was cooked with channels
    /// (<see cref="CookedSection.HasAtlas"/>), its mesh is already UV-seam-split and the atlas bytes ride in
    /// the blob — the presenter uploads them to a <see cref="Texture2DArray"/> and binds it, rather than
    /// re-running channel generation. When the section was cooked without channels, presentation matches a
    /// plain <see cref="SectionCompiler"/> compile.</para>
    /// </summary>
    public sealed class GameObjectSectionPresenter : ISectionPresenter
    {
        readonly SectionCompilationSettings _settings;

        /// <param name="settings">LOD/skirt/collision/material template. Channel generation is driven by the
        /// cook (the presenter forces it off and binds the prebaked atlas instead).</param>
        public GameObjectSectionPresenter(SectionCompilationSettings settings)
        {
            _settings = settings ?? new SectionCompilationSettings();
        }

        sealed class Handle : ISectionHandle
        {
            public int3 Coord { get; set; }
            public CompiledSection Compiled;
            public Texture2DArray PrebakedAtlas; // owned here only when not handed to CompiledSection
        }

        public ISectionHandle Present(CookedSection cooked, Transform root)
        {
            // The cook owns channel generation; never regenerate during presentation.
            var settings = Clone(_settings);
            settings.GenerateChannels = false;

            // Fast path: the cook already baked the skirt + LOD chain on a worker thread, so the present is
            // upload-only (no main-thread simplification — the profiler's ~68 ms/section bottleneck). Falls
            // back to the full compile only for sections cooked without baked LODs.
            // Use the cook-baked collision mesh (unskirted, simplified) when present; else the full mesh.
            MeshData collisionSource = cooked.HasBakedCollision ? cooked.CollisionMesh : cooked.Mesh;

            CompiledSection compiled = cooked.HasBakedLods
                ? SectionCompiler.CompilePrebaked(cooked.Lods, collisionSource, cooked.Dims, cooked.Coord, settings, root)
                : SectionCompiler.CompileSection(cooked.Mesh, cooked.Weights, cooked.Dims, cooked.Coord, settings, root);

            Texture2DArray atlas = null;
            if (cooked.HasAtlas)
            {
                atlas = BuildAtlasTexture(cooked);
                // Hand the texture to the CompiledSection so its Dispose() destroys it with the rest.
                compiled.ChannelTexture = atlas;
                foreach (var renderer in compiled.Root.GetComponentsInChildren<MeshRenderer>())
                    ChannelPacking.ApplyToRenderer(renderer, atlas, cooked.ChannelTable, cooked.ChannelTexcoordMetrics);
            }

            return new Handle { Coord = cooked.Coord, Compiled = compiled, PrebakedAtlas = atlas };
        }

        public void Release(ISectionHandle handle)
        {
            if (handle is Handle h)
            {
                // CompiledSection.Dispose destroys the GO, owned meshes, and ChannelTexture (the atlas we set).
                h.Compiled?.Dispose();
                h.Compiled = null;
                h.PrebakedAtlas = null;
            }
        }

        /// <summary>Rebuilds the R8 <see cref="Texture2DArray"/> from the cooked atlas blob.</summary>
        static Texture2DArray BuildAtlasTexture(CookedSection cooked)
        {
            int res = cooked.ChannelAtlasResolution;
            int slices = math.max(1, cooked.ChannelAtlasSlices);
            var tex = new Texture2DArray(res, res, slices, TextureFormat.R8, mipChain: true, linear: true)
            {
                name = $"SectionChannels_{cooked.Coord.x}_{cooked.Coord.y}_{cooked.Coord.z}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            int sliceBytes = res * res;
            var px = new Color32[sliceBytes];
            for (int s = 0; s < slices; s++)
            {
                int offset = s * sliceBytes;
                for (int i = 0; i < sliceBytes; i++)
                {
                    byte b = cooked.ChannelAtlasBlob[offset + i];
                    px[i] = new Color32(b, b, b, 255);
                }
                tex.SetPixels32(px, s);
            }
            tex.Apply(updateMipmaps: true);
            return tex;
        }

        static SectionCompilationSettings Clone(SectionCompilationSettings s)
        {
            return new SectionCompilationSettings
            {
                Material = s.Material,
                GenerateCollision = s.GenerateCollision,
                GenerateLODs = s.GenerateLODs,
                LODQualities = s.LODQualities,
                LODScreenRelativeTransitionHeights = s.LODScreenRelativeTransitionHeights,
                Skirt = s.Skirt,
                GenerateChannels = s.GenerateChannels,
                ChannelUVSettings = s.ChannelUVSettings,
                ChannelRasterizer = s.ChannelRasterizer,
                ChannelGutterFill = s.ChannelGutterFill,
            };
        }
    }
}
