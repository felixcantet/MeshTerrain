using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain.Demo
{
    /// <summary>
    /// Visual verification harness for Phases 0–2. Put it on an empty GameObject, pick a Mode + Material,
    /// and it runs the real pipeline and spawns one colored GameObject per section so you can eyeball the
    /// result — without entering Play mode. Use the context-menu "Rebuild" to force a rebuild.
    ///
    /// Ships in the runtime assembly so it is versioned with the package, but it only renders gray-box
    /// debug geometry; it is a development/verification aid, not production content.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Mesh Terrain/Mesh Terrain Demo")]
    public class MeshTerrainDemo : MonoBehaviour
    {
        public enum Mode
        {
            Phase0_DisplayPlane,      // build a MeshData by hand -> display it
            Phase1_PartitionColored,  // partition a plane -> one colored GO per section
            Phase2_NoisePartition,    // modifier stack (Rectangle + Noise + paint) -> partition -> colored
            Phase3_CompiledSections,  // modifier stack -> partition -> LOD/skirt/collision section compiler
            Phase4_ChannelAtlas,      // modifier stack -> partition -> compile w/ channel atlas + URP material
        }

        [Header("What to build")]
        public Mode mode = Mode.Phase2_NoisePartition;
        public Material material;          // assign any URP/Standard lit material

        [Header("Mesh")]
        public int resolution = 64;        // quads per side
        public float size = 400f;

        [Header("Partition")]
        public float cellSize = 100f;

        [Header("Noise (Phase 2)")]
        public float noiseIntensity = 25f;
        public float noiseFrequency = 12f;
        public string paintChannel = "Grass";
        public float paintRadius = 120f;
        public float paintFalloff = 60f;

        [Header("Section Compilation (Phase 3)")]
        public bool generateLODs = true;
        public bool generateSkirts = true;
        public bool generateCollision = true;
        public float skirtWidth = 0f;       // 0 = cell-size default
        public float skirtPushDown = 0f;    // 0 = cell-size default
        public float[] lodQualities = { 1f, 0.5f, 0.25f };
        public float[] lodTransitionHeights = { 0.6f, 0.3f, 0.1f };

        [Header("Channel Atlas (Phase 4)")]
        public bool generateChannels = true;
        public bool channelGutterFill = true;
        public bool useComputeRasterizer = false;  // Phase 4b: GPU backend (falls back to CPU if unsupported)
        public float channelTexelSize = 20f;   // smaller = higher-res atlas for the demo plane
        [Tooltip("Tint compiled sections by the section debug color in addition to the channel material.")]
        public bool tintSections = false;

        // Tracked so we can fully tear down between rebuilds. Meshes are tracked separately because
        // destroying a GameObject does NOT free the Mesh it referenced — that was the "old meshes still
        // in the scene" leak.
        readonly List<GameObject> _spawned = new();
        readonly List<Mesh> _meshes = new();
        readonly List<CompiledSection> _compiledSections = new();

        void OnEnable() => RequestRebuild();

        void OnValidate()
        {
            // OnValidate runs during deserialization, where DestroyImmediate is illegal (Unity skips it,
            // so old children survive while new ones spawn -> accumulation). Defer the rebuild until it is
            // safe. In the editor that means EditorApplication.delayCall; at runtime OnValidate is not a
            // concern, so rebuild directly.
            if (!isActiveAndEnabled) return;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.delayCall += DeferredRebuild;
#else
            Rebuild();
#endif
        }

#if UNITY_EDITOR
        void DeferredRebuild()
        {
            // The component may have been destroyed/disabled between scheduling and firing.
            if (this == null || !isActiveAndEnabled) return;
            Rebuild();
        }
#endif

        void RequestRebuild()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorApplication.delayCall += DeferredRebuild;
                return;
            }
#endif
            Rebuild();
        }

        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            Clear();
            switch (mode)
            {
                case Mode.Phase0_DisplayPlane: BuildPhase0(); break;
                case Mode.Phase1_PartitionColored: BuildPhase1(); break;
                case Mode.Phase2_NoisePartition: BuildPhase2(); break;
                case Mode.Phase3_CompiledSections: BuildPhase3(); break;
                case Mode.Phase4_ChannelAtlas: BuildPhase4(); break;
            }
        }

        void BuildPhase0()
        {
            var mesh = MakePlane(resolution, size, Allocator.TempJob);
            try { SpawnSection(mesh, "Phase0_Plane", Color.gray); }
            finally { mesh.Dispose(); }
        }

        void BuildPhase1()
        {
            var src = MakePlane(resolution, size, Allocator.TempJob);
            var grid = new GridSettings { CellSize = cellSize, Is2D = true };
            var result = MeshPartitioner.Partition(src, grid, allocator: Allocator.TempJob);
            try
            {
                for (int i = 0; i < result.SectionCount; i++)
                    SpawnSection(result.Sections[i], $"Section_{result.SectionCoords[i]}", ColorFor(i));
            }
            finally { result.Dispose(); src.Dispose(); }
        }

        void BuildPhase2()
        {
            var rect = new RectangleBaseModifier
            {
                Resolution = new int2(resolution, resolution),
                Size = new float2(size, size),
            };
            var noise = new NoiseModifier
            {
                UnscaledCoverage = new float3(size * 1.5f, 400f, size * 1.5f),
                Intensity = noiseIntensity,
                DisplacementType = NoiseType.Fbm,
                NoiseFrequency = new double2(noiseFrequency, noiseFrequency),
                Falloff = 0.1,
            };
            var paint = new WeightUtilityModifier
            {
                WeightChannelName = paintChannel,
                Radius = paintRadius, Falloff = paintFalloff,
                InnerValue = 1f, OuterValue = 0f,
            };

            var stack = new List<ModifierComponent> { rect, noise, paint };
            var built = ModifierGroup.Process(stack, float4x4.identity, Allocator.TempJob);
            try
            {
                var grid = new GridSettings { CellSize = cellSize, Is2D = true };
                var result = MeshPartitioner.Partition(built.Mesh, grid, built.Weights, Allocator.TempJob);
                try
                {
                    for (int i = 0; i < result.SectionCount; i++)
                        SpawnSection(result.Sections[i], $"Section_{result.SectionCoords[i]}", ColorFor(i));
                }
                finally { result.Dispose(); }
            }
            finally { built.Dispose(); }
        }


        void BuildPhase3()
        {
            var rect = new RectangleBaseModifier
            {
                Resolution = new int2(resolution, resolution),
                Size = new float2(size, size),
            };
            var noise = new NoiseModifier
            {
                UnscaledCoverage = new float3(size * 1.5f, 400f, size * 1.5f),
                Intensity = noiseIntensity,
                DisplacementType = NoiseType.Fbm,
                NoiseFrequency = new double2(noiseFrequency, noiseFrequency),
                Falloff = 0.1,
            };
            var paint = new WeightUtilityModifier
            {
                WeightChannelName = paintChannel,
                Radius = paintRadius, Falloff = paintFalloff,
                InnerValue = 1f, OuterValue = 0f,
            };

            var stack = new List<ModifierComponent> { rect, noise, paint };
            var built = ModifierGroup.Process(stack, float4x4.identity, Allocator.TempJob);
            try
            {
                var grid = new GridSettings { CellSize = cellSize, Is2D = true };
                var result = MeshPartitioner.Partition(built.Mesh, grid, built.Weights, Allocator.TempJob);
                try
                {
                    var settings = new SectionCompilationSettings
                    {
                        Material = material,
                        GenerateLODs = generateLODs,
                        GenerateCollision = generateCollision,
                        LODQualities = lodQualities,
                        LODScreenRelativeTransitionHeights = lodTransitionHeights,
                        Skirt = MeshSkirtSettings.DefaultForCellSize(cellSize),
                    };
                    settings.Skirt.Enabled = generateSkirts;
                    if (skirtWidth > 0f) settings.Skirt.Width = skirtWidth;
                    if (skirtPushDown > 0f) settings.Skirt.PushDown = skirtPushDown;

                    var compiled = SectionCompiler.Compile(result, settings, transform);
                    for (int i = 0; i < compiled.Length; i++)
                    {
                        _compiledSections.Add(compiled[i]);
                        ApplySectionColor(compiled[i], ColorFor(i));
                    }
                }
                finally { result.Dispose(); }
            }
            finally { built.Dispose(); }
        }

        void BuildPhase4()
        {
            var rect = new RectangleBaseModifier
            {
                Resolution = new int2(resolution, resolution),
                Size = new float2(size, size),
            };
            var noise = new NoiseModifier
            {
                UnscaledCoverage = new float3(size * 1.5f, 400f, size * 1.5f),
                Intensity = noiseIntensity,
                DisplacementType = NoiseType.Fbm,
                NoiseFrequency = new double2(noiseFrequency, noiseFrequency),
                Falloff = 0.1,
            };
            var paint = new WeightUtilityModifier
            {
                WeightChannelName = paintChannel,
                Radius = paintRadius, Falloff = paintFalloff,
                InnerValue = 1f, OuterValue = 0f,
            };

            var stack = new List<ModifierComponent> { rect, noise, paint };
            var built = ModifierGroup.Process(stack, float4x4.identity, Allocator.TempJob);
            try
            {
                var grid = new GridSettings { CellSize = cellSize, Is2D = true };
                var result = MeshPartitioner.Partition(built.Mesh, grid, built.Weights, Allocator.TempJob);
                try
                {
                    var settings = new SectionCompilationSettings
                    {
                        Material = material,
                        GenerateLODs = generateLODs,
                        GenerateCollision = generateCollision,
                        LODQualities = lodQualities,
                        LODScreenRelativeTransitionHeights = lodTransitionHeights,
                        Skirt = MeshSkirtSettings.DefaultForCellSize(cellSize),
                        GenerateChannels = generateChannels,
                        ChannelGutterFill = channelGutterFill,
                        ChannelRasterizer = useComputeRasterizer
                            ? ChannelRasterizerBackend.Compute
                            : ChannelRasterizerBackend.CPU,
                    };
                    settings.Skirt.Enabled = generateSkirts;
                    if (skirtWidth > 0f) settings.Skirt.Width = skirtWidth;
                    if (skirtPushDown > 0f) settings.Skirt.PushDown = skirtPushDown;
                    settings.ChannelUVSettings = ChannelUVSettings.Default;
                    settings.ChannelUVSettings.TexelSize3D = channelTexelSize;

                    var compiled = SectionCompiler.Compile(result, settings, transform);
                    for (int i = 0; i < compiled.Length; i++)
                    {
                        _compiledSections.Add(compiled[i]);
                        if (tintSections)
                            ApplySectionColor(compiled[i], ColorFor(i));
                    }
                }
                finally { result.Dispose(); }
            }
            finally { built.Dispose(); }
        }
        // ---- helpers ----

        static MeshData MakePlane(int cells, float size, Allocator alloc)
        {
            int verts = (cells + 1) * (cells + 1);
            int tris = cells * cells * 2;
            var d = MeshData.Allocate(verts, tris, alloc, withNormals: true, withChannelUVs: false,
                withSourceUV0: true, withBaseIDs: true);
            float step = size / cells;
            for (int z = 0; z <= cells; z++)
                for (int x = 0; x <= cells; x++)
                {
                    int i = z * (cells + 1) + x;
                    d.Vertices[i] = new float3(x * step - size * 0.5f, 0f, z * step - size * 0.5f);
                    d.Normals[i] = new float3(0, 1, 0);
                    d.SourceUV0[i] = new float2((float)x / cells, (float)z / cells);
                }
            int t = 0;
            for (int z = 0; z < cells; z++)
                for (int x = 0; x < cells; x++)
                {
                    int v0 = z * (cells + 1) + x, v1 = v0 + 1, v2 = v0 + (cells + 1), v3 = v2 + 1;
                    d.Triangles[t] = new int3(v0, v2, v1); d.BaseIDLayer[t++] = 0;
                    d.Triangles[t] = new int3(v1, v2, v3); d.BaseIDLayer[t++] = 0;
                }
            return d;
        }

        void SpawnSection(in MeshData section, string name, Color color)
        {
            var mesh = MeshDataConversions.ToRenderMesh(section, name);
            _meshes.Add(mesh);

            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor("_BaseColor", color); // URP Lit
            mpb.SetColor("_Color", color);     // Standard / fallback
            mr.SetPropertyBlock(mpb);
            _spawned.Add(go);
        }

        static Color ColorFor(int i)
        {
            // Golden-ratio hue stepping so adjacent sections differ.
            return Color.HSVToRGB((i * 0.61803398875f) % 1f, 0.65f, 0.95f);
        }


        static void ApplySectionColor(CompiledSection section, Color color)
        {
            if (section?.Root == null) return;
            // Read-modify-write per renderer so we don't clobber channel-atlas props (Phase 4).
            var mpb = new MaterialPropertyBlock();
            foreach (var renderer in section.Root.GetComponentsInChildren<MeshRenderer>())
            {
                renderer.GetPropertyBlock(mpb);
                mpb.SetColor("_BaseColor", color);
                mpb.SetColor("_Color", color);
                renderer.SetPropertyBlock(mpb);
            }
        }
        void Clear()
        {
            foreach (var compiled in _compiledSections) compiled.Dispose();
            _compiledSections.Clear();

            // Destroy spawned GameObjects, plus any orphan children left after a domain reload (where
            // _spawned was cleared but the child GameObjects persisted).
            foreach (var go in _spawned) DestroyObject(go);
            _spawned.Clear();

            // Free the generated meshes we still track (destroying the GameObject does NOT free its Mesh).
            foreach (var mesh in _meshes) DestroyObject(mesh);
            _meshes.Clear();

            // Sweep orphan children left after a domain reload (where the tracking lists were cleared but
            // the child GameObjects + their meshes persisted). Free each child's mesh before the GO.
            for (int c = transform.childCount - 1; c >= 0; c--)
            {
                var child = transform.GetChild(c).gameObject;
                if (child.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null)
                    DestroyObject(mf.sharedMesh);
                DestroyObject(child);
            }
        }

        static void DestroyObject(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }

        void OnDisable() => Clear();
    }
}
