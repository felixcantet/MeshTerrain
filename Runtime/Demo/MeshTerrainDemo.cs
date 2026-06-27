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
            Phase5_Streaming,         // MeshTerrainStreamer: cook-on-miss + cache + distance-driven load/unload
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

        [Header("Streaming (Phase 5)")]
        [Tooltip("Focus transform driving streaming (defaults to Camera.main at runtime).")]
        public Transform streamFocus;
        [Tooltip("Streaming cell size (world units). Larger = fewer, bigger tiles -> far fewer GameObjects/" +
                 "uploads/colliders in the load ring (the present cost is per-tile and main-thread).")]
        public float streamCellSize = 200f;
        [Tooltip("Quads per side per streaming tile. Lower = lighter mesh upload per tile.")]
        public int streamTileResolution = 32;
        [Tooltip("Half-extent of the streamed world (world units); the base rectangle spans 2x this.")]
        public float streamWorldExtent = 2000f;
        public float loadDistance = 500f;
        public float unloadDistance = 1000f;   // >=2 cells of hysteresis band to avoid boundary thrash
        public int maxConcurrentCooks = 4;
        [Tooltip("Hard cap on tiles presented per frame (each present is a main-thread mesh upload + collider). " +
                 "Present is ~1.4ms now, so a few per frame fills the ring faster while staying smooth.")]
        public int maxPresentsPerFrame = 3;
        public float maxMillisPerFrame = 6f;
        [Tooltip("Collision mesh quality: 1 = full-res, lower = cheaper PhysX cook (collision baked unskirted in the cook).")]
        public float collisionQuality = 0.25f;
        public float worldHeight = 4000f;
        [Tooltip("Record per-phase timings; press P in Play mode to dump the full report to the Console.")]
        public bool streamProfiling = true;

        public enum PresenterBackend { GameObject, BatchRendererGroup }
        [Tooltip("Rendering backend: GameObject (per-section MeshRenderer/LODGroup) or BatchRendererGroup " +
                 "(GPU-instanced, no GameObjects — milestone 5.6a prototype, flat material, LOD0 only).")]
        public PresenterBackend presenterBackend = PresenterBackend.GameObject;
        [Tooltip("Flat instanced material for the BRG backend (uses 'Mesh Terrain/Instanced Flat' if unset).")]
        public Material instancedMaterial;
        [Tooltip("BRG shared-atlas resolution. >0 enables shared-atlas instanced channels (all sections one " +
                 "material -> draws stop scaling with section count). Must be a fixed size. 0 = flat (no channels).")]
        public int brgAtlasResolution = 256;
        [Tooltip("Max slices in the BRG shared channel atlas (sections * channels).")]
        public int brgAtlasCapacity = 1024;
        [Tooltip("BRG debug: force LOD0 for all sections (isolates whether LOD simplification corrupts channel UVs).")]
        public bool brgForceLod0 = false;
        [Tooltip("Per-channel material layers (albedo/normal/mask + tiling). Layer i blends where channel i's " +
                 "weight is high. Empty = flat debug colors. The demo paints channel 0 ('Grass'), so layer 0 shows.")]
        public List<TerrainLayer> channelLayers = new();
        [Tooltip("Resolution of the built terrain layer Texture2DArrays.")]
        public int layerArrayResolution = 512;
        [Tooltip("BRG backend: tint each tile by its selected LOD (green=LOD0, yellow=LOD1, red=LOD2+) so " +
                 "LOD selection is visible (BRG draws don't show in Scene wireframe). Off = channel blend.")]
        public bool debugLodColors = false;

        // Tracked so we can fully tear down between rebuilds. Meshes are tracked separately because
        // destroying a GameObject does NOT free the Mesh it referenced — that was the "old meshes still
        // in the scene" leak.
        readonly List<GameObject> _spawned = new();
        readonly List<Mesh> _meshes = new();
        readonly List<CompiledSection> _compiledSections = new();
        Fca.MeshTerrain.Streaming.MeshTerrainStreamer _streamer;
        Fca.MeshTerrain.Streaming.BatchRendererGroupSectionPresenter _brgPresenter;

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
                case Mode.Phase5_Streaming: BuildPhase5(); break;
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
        void BuildPhase5()
        {
            // Spawn (or reuse) a streamer on a child GO and drive it with a code-supplied modifier stack.
            // Streaming itself runs in Play mode (the streamer's Update). In edit mode this just configures it.
            // Build the GO INACTIVE so the streamer's OnEnable (which caches cook options incl. the fixed
            // atlas resolution) doesn't fire until all fields below are set. Activated at the end.
            var go = new GameObject("MeshTerrainStreamer") { hideFlags = HideFlags.DontSave };
            go.SetActive(false);
            go.transform.SetParent(transform, false);
            _spawned.Add(go);

            _streamer = go.AddComponent<Fca.MeshTerrain.Streaming.MeshTerrainStreamer>();
            _streamer.Focus = streamFocus;
            _streamer.WorldOriginOffset = Vector3.zero;
            _streamer.WorldHeight = worldHeight;
            _streamer.LoadDistance = loadDistance;
            _streamer.UnloadDistance = unloadDistance;
            _streamer.MaxConcurrentCooks = maxConcurrentCooks;
            _streamer.MaxPresentsPerFrame = maxPresentsPerFrame;
            _streamer.MaxMillisPerFrame = maxMillisPerFrame;
            _streamer.GenerateChannels = generateChannels;
            // When using the BRG backend with channels, force the fixed atlas resolution so all section
            // atlases fit the one shared Texture2DArray.
            _streamer.FixedAtlasResolution = (presenterBackend == PresenterBackend.BatchRendererGroup && generateChannels)
                ? brgAtlasResolution : 0;
            _streamer.RamCapacity = 256;

            var settings = new SectionCompilationSettings
            {
                Material = material,
                GenerateLODs = generateLODs,
                GenerateCollision = generateCollision,
                CollisionQuality = collisionQuality,
                LODQualities = lodQualities,
                LODScreenRelativeTransitionHeights = lodTransitionHeights,
                Skirt = MeshSkirtSettings.DefaultForCellSize(streamCellSize),
                GenerateChannels = generateChannels,
                ChannelGutterFill = channelGutterFill,
                ChannelRasterizer = useComputeRasterizer
                    ? ChannelRasterizerBackend.Compute
                    : ChannelRasterizerBackend.CPU,
            };
            settings.Skirt.Enabled = generateSkirts;
            settings.ChannelUVSettings = ChannelUVSettings.Default;
            settings.ChannelUVSettings.TexelSize3D = channelTexelSize;

            if (presenterBackend == PresenterBackend.BatchRendererGroup)
            {
                int atlasRes = generateChannels ? brgAtlasResolution : 0;
                _brgPresenter = new Fca.MeshTerrain.Streaming.BatchRendererGroupSectionPresenter(
                    instancedMaterial, transform, lodTransitionHeights, atlasRes, brgAtlasCapacity);
                _brgPresenter.DebugLodColors = debugLodColors;
                _brgPresenter.ForceLod0 = brgForceLod0;
                if (channelLayers != null && channelLayers.Count > 0)
                    _brgPresenter.SetTerrainLayers(channelLayers, layerArrayResolution);
                _brgPresenter.SetPickingObject(go); // editor click/box-select the streamer GO via its sections
                _streamer.SetPresenter(_brgPresenter);
            }
            else
            {
                _streamer.SetPresenter(new Fca.MeshTerrain.Streaming.GameObjectSectionPresenter(settings));
            }

            // Larger streaming cells -> fewer, bigger tiles in the load ring.
            var def = ScriptableObject.CreateInstance<MeshPartitionDefinition>();
            def.hideFlags = HideFlags.DontSave;
            def.name = "DemoStreamingDef";
            def.CellSize = (uint)Mathf.Max(1f, streamCellSize);
            def.Is2D = true;
            def.Material = material;
            def.ChannelTexelSize = channelTexelSize;
            // Declare the GLOBAL channel names in a fixed order so the shared atlas indexes slices by global
            // channel (slice i == channel i for every section). Index 0 = the painted channel; pad to the
            // number of assigned material layers so each layer maps to a stable global channel.
            int channelCount = Mathf.Max(1, channelLayers != null ? channelLayers.Count : 1);
            def.ChannelNames = new List<string> { paintChannel };
            for (int i = 1; i < channelCount; i++) def.ChannelNames.Add($"Channel{i}");
            _streamer.Definition = def;

            _streamer.SetModifierStack(BuildStreamingStack());
            Fca.MeshTerrain.Streaming.StreamingDiagnostics.Reset();
            Fca.MeshTerrain.Streaming.StreamingProfiler.Reset();
            Fca.MeshTerrain.Streaming.StreamingProfiler.Enabled = streamProfiling;

            // Now that every field (incl. FixedAtlasResolution + Definition + stack) is set, activate the GO
            // so the streamer's OnEnable -> EnsureInitialized caches the correct cook options.
            go.SetActive(true);
        }

        List<ModifierComponent> BuildStreamingStack()
        {
            // The base rectangle spans the whole streamed world; its resolution is chosen so each streaming
            // cell carries ~streamTileResolution quads/side (keeps per-tile mesh upload light).
            float worldSpan = streamWorldExtent * 2f;
            int cellsPerSide = Mathf.Max(1, Mathf.RoundToInt(worldSpan / Mathf.Max(1f, streamCellSize)));
            int totalQuads = Mathf.Max(1, cellsPerSide * Mathf.Max(1, streamTileResolution));
            var rect = new RectangleBaseModifier
            {
                Resolution = new int2(totalQuads, totalQuads),
                Size = new float2(worldSpan, worldSpan),
            };
            var noise = new NoiseModifier
            {
                UnscaledCoverage = new float3(worldSpan * 1.5f, 400f, worldSpan * 1.5f),
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

            // If a 2nd material layer is assigned, paint a 2nd channel ("Channel1") overlapping the first so
            // the layer BLEND is actually visible (channel 0 fades into channel 1 in the overlap).
            if (channelLayers != null && channelLayers.Count >= 2)
            {
                var paint2 = new WeightUtilityModifier
                {
                    WeightChannelName = "Channel1",
                    Center = new float3(paintRadius * 0.8f, 0, 0), // offset so it overlaps channel 0
                    Radius = paintRadius, Falloff = paintFalloff,
                    InnerValue = 1f, OuterValue = 0f,
                };
                stack.Add(paint2);
            }
            return stack;
        }

        void OnDrawGizmosSelected()
        {
            if (mode != Mode.Phase5_Streaming) return;
            Transform f = streamFocus != null ? streamFocus : (Camera.main != null ? Camera.main.transform : null);
            if (f == null) return;
            Vector3 c = f.position; c.y = 0f;
            Gizmos.color = Color.green; DrawRing(c, loadDistance);
            Gizmos.color = Color.yellow; DrawRing(c, unloadDistance);
        }

        static void DrawRing(Vector3 center, float radius)
        {
            const int seg = 48;
            Vector3 prev = center + new Vector3(radius, 0, 0);
            for (int i = 1; i <= seg; i++)
            {
                float a = (i / (float)seg) * Mathf.PI * 2f;
                Vector3 p = center + new Vector3(Mathf.Cos(a) * radius, 0, Mathf.Sin(a) * radius);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
        }

        void Update()
        {
            if (mode != Mode.Phase5_Streaming || !Application.isPlaying || _streamer == null) return;
            if (Input.GetKeyDown(KeyCode.P))
                Debug.Log(_streamer.ProfilerReport());
        }

        void OnGUI()
        {
            if (mode != Mode.Phase5_Streaming || _streamer == null) return;
            int hits = Fca.MeshTerrain.Streaming.StreamingDiagnostics.CacheHits;
            int misses = Fca.MeshTerrain.Streaming.StreamingDiagnostics.CacheMisses;
            int cooks = Fca.MeshTerrain.Streaming.StreamingDiagnostics.Cooks;

            GUILayout.BeginArea(new Rect(10, 10, 360, 220), GUI.skin.box);
            GUILayout.Label($"Resident: {_streamer.residentCount}   Queued: {_streamer.queuedCount}   In-flight: {_streamer.pendingCount}");
            GUILayout.Label($"Live sections: {Fca.MeshTerrain.Streaming.StreamingDiagnostics.LiveSections}");
            GUILayout.Label($"Reused (cache hit): {hits}   Cooked (miss): {misses}   Cooks: {cooks}");
            GUILayout.Label($"Cooks cancelled (churn): {Fca.MeshTerrain.Streaming.StreamingDiagnostics.CooksCancelled}");
            float reuseRate = (hits + misses) > 0 ? 100f * hits / (hits + misses) : 0f;
            GUILayout.Label($"Reuse rate: {reuseRate:F0}%   (high = baked tiles served, no re-pipeline)");
            GUILayout.Label($"Last finalize: {_streamer.lastFinalizeMs:F1} ms for {_streamer.lastFinalizedCount} section(s)");

            // Top phases inline so the bottleneck is visible without the Console dump.
            var phases = Fca.MeshTerrain.Streaming.StreamingProfiler.Phases;
            if (phases.Count > 0)
            {
                GUILayout.Label("Phases (avg ms / max ms):");
                foreach (var kvp in phases)
                    GUILayout.Label($"  {kvp.Key}: {kvp.Value.AvgMs:F2} / {kvp.Value.MaxMs:F2}  (n={kvp.Value.Count})");
            }
            GUILayout.Label("Press P to dump full report to Console.");
            GUILayout.EndArea();
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
            // The streamer tears itself down (OnDisable -> ForceUnloadAll + cache dispose) when its GO is
            // destroyed below; drop the managed Definition we created for it.
            if (_streamer != null)
            {
                if (_streamer.Definition != null) DestroyObject(_streamer.Definition);
                _streamer.ForceUnloadAll();   // releases all section handles (BRG slots) before we dispose the BRG
                _streamer = null;
            }

            // Dispose the BRG presenter (BatchRendererGroup + GraphicsBuffer) after sections are released.
            if (_brgPresenter != null)
            {
                _brgPresenter.Dispose();
                _brgPresenter = null;
            }

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
