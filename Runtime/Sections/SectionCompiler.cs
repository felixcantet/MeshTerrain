using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityMeshSimplifier;

namespace Fca.MeshTerrain
{
    public enum MeshSkirtPushMethod
    {
        FixedDown,
        VertexNormal,
    }

    [Serializable]
    public struct MeshSkirtSettings
    {
        public bool Enabled;
        public float Width;
        public float PushDown;
        public MeshSkirtPushMethod PushMethod;
        public float VertexSnapTolerance;
        public float BoundaryMinPerimeter;

        public static MeshSkirtSettings DefaultForCellSize(float cellSize)
            => new MeshSkirtSettings
            {
                Enabled = true,
                // Width is the OUTWARD in-plane offset of the skirt ring (UE FMeshSkirtSettings::Width). It makes
                // a tile's skirt overhang its neighbour's border, so the seam stays covered even when the two
                // tiles' edges don't align (e.g. different LODs). Sized to ~one cell-edge sample so the overhang
                // comfortably bridges a neighbour at a coarser LOD. UE ships a flat 10; we scale with the cell.
                Width = math.max(cellSize * 0.1f, 1f),
                PushDown = math.max(cellSize * 0.05f, 1f),
                PushMethod = MeshSkirtPushMethod.FixedDown,
                VertexSnapTolerance = 1e-4f,
                BoundaryMinPerimeter = 0f,
            };
    }

    /// <summary>Which backend rasterizes the channel atlas. Phase 4a honors CPU; GPU lands in 4b.</summary>
    public enum ChannelRasterizerBackend
    {
        CPU,
        Compute,
    }

    [Serializable]
    public sealed class SectionCompilationSettings
    {
        public Material Material;
        public bool GenerateCollision = true;
        public bool GenerateLODs = true;
        public float[] LODQualities = { 1f, 0.5f, 0.25f };
        public float[] LODScreenRelativeTransitionHeights = { 0.6f, 0.3f, 0.1f };
        public MeshSkirtSettings Skirt = MeshSkirtSettings.DefaultForCellSize(100f);

        /// <summary>
        /// Simplification quality of the streaming collision mesh (1 = full-res, lower = far cheaper PhysX
        /// cook). The collision mesh is baked unskirted on the cook's worker thread; the present only uploads
        /// + bakes PhysX. The full-res PhysX bake + upload was ~13 ms/section in streaming (doc/08 §8).
        /// </summary>
        public float CollisionQuality = 0.25f;

        // --- Phase 4: channel texture atlas ---

        /// <summary>Generate channel UVs, rasterize the weight layers into an atlas, and bind it per renderer.</summary>
        public bool GenerateChannels = false;
        public ChannelUVSettings ChannelUVSettings = ChannelUVSettings.Default;
        public ChannelRasterizerBackend ChannelRasterizer = ChannelRasterizerBackend.CPU;
        /// <summary>Whether to run the pull-push gutter fill (the border fill always runs).</summary>
        public bool ChannelGutterFill = true;

        public static SectionCompilationSettings FromDefinition(MeshPartitionDefinition definition)
        {
            float cellSize = definition != null ? definition.CellSize : 100f;
            var settings = new SectionCompilationSettings
            {
                Material = definition != null ? definition.Material : null,
                Skirt = MeshSkirtSettings.DefaultForCellSize(cellSize),
            };
            if (definition != null)
                settings.ChannelUVSettings.TexelSize3D = definition.ChannelTexelSize;
            return settings;
        }
    }

    public sealed class CompiledSection : IDisposable
    {
        readonly List<Mesh> _ownedMeshes = new();

        public GameObject Root { get; internal set; }
        public int3 Coord { get; internal set; }
        public Mesh[] RenderMeshes { get; internal set; }
        public Mesh CollisionMesh { get; internal set; }

        /// <summary>The rasterized channel atlas (Phase 4), or null when channels were not generated.</summary>
        public Texture ChannelTexture { get; internal set; }

        internal void Own(Mesh mesh)
        {
            if (mesh != null)
                _ownedMeshes.Add(mesh);
        }

        public void Dispose()
        {
            if (Root != null)
                DestroyObject(Root);

            foreach (var mesh in _ownedMeshes)
                DestroyObject(mesh);

            if (ChannelTexture != null)
                DestroyObject(ChannelTexture);

            _ownedMeshes.Clear();
            Root = null;
            RenderMeshes = null;
            CollisionMesh = null;
            ChannelTexture = null;
        }

        static void DestroyObject(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
            else UnityEngine.Object.DestroyImmediate(obj);
        }
    }

    public static class SectionCompiler
    {
        const int MaxPackedWeightLayers = 24;

        public static CompiledSection[] Compile(
            in PartitionResult partition,
            SectionCompilationSettings settings,
            Transform parent = null)
        {
            var results = new CompiledSection[partition.SectionCount];
            for (int i = 0; i < partition.SectionCount; i++)
            {
                WeightLayerSet weights = partition.SectionWeights != null && i < partition.SectionWeights.Length
                    ? partition.SectionWeights[i]
                    : null;
                results[i] = CompileSection(
                    partition.Sections[i],
                    weights,
                    partition.Dims,
                    partition.SectionCoords[i],
                    settings,
                    parent);
            }
            return results;
        }

        public static CompiledSection CompileSection(
            in MeshData section,
            WeightLayerSet weights,
            in GridDimensions dims,
            int3 coord,
            SectionCompilationSettings settings,
            Transform parent = null)
        {
            settings ??= new SectionCompilationSettings();
            ValidateWeights(weights);

            float3 origin = SectionOrigin(dims, coord);
            var compiled = new CompiledSection { Coord = coord };
            var root = new GameObject($"Section_{coord.x}_{coord.y}_{coord.z}") { hideFlags = HideFlags.DontSave };
            root.transform.SetParent(parent, false);
            root.transform.localPosition = (Vector3)origin;
            compiled.Root = root;

            MeshData localSection = CopyWithOffset(section, origin, Allocator.TempJob);

            // Channel generation (Phase 4) runs before skirt/LOD so the generated ChannelUVs ride
            // through the copy/skirt/pack paths (which already carry ChannelUVs). It produces a
            // fresh, UV-seam-split mesh + remapped weights used for rendering; collision keeps the
            // original unsplit localSection. The collision mesh is built up-front for that reason.
            Mesh collisionMesh = null;
            if (settings.GenerateCollision)
                collisionMesh = MeshDataConversions.ToCollisionMesh(localSection, root.name + "_Collision");

            MeshData baseRenderSection = localSection;
            WeightLayerSet baseRenderWeights = weights;
            MeshData channelSection = default;
            WeightLayerSet channelWeights = null;
            ChannelRasterResult raster = null;

            MeshData renderData = default;
            WeightLayerSet renderWeights = null;

            var _sw = System.Diagnostics.Stopwatch.StartNew();
            double _t0 = 0;
            try
            {
                if (settings.GenerateChannels)
                {
                    channelSection = ChannelUVUnwrap.Generate(
                        localSection, weights, settings.ChannelUVSettings, Allocator.TempJob,
                        out channelWeights, out var mapping);
                    baseRenderSection = channelSection;
                    baseRenderWeights = channelWeights;

                    if (settings.ChannelRasterizer == ChannelRasterizerBackend.Compute)
                    {
                        raster = ChannelRasterizerGPU.Render(
                            channelSection, channelWeights, mapping, settings.ChannelGutterFill);
                        if (raster == null)
                            Debug.LogWarning("Compute channel rasterizer unavailable (no compute support); using CPU backend.");
                    }

                    raster ??= ChannelRasterizerCPU.Render(
                        channelSection, channelWeights, mapping, settings.ChannelGutterFill);
                    compiled.ChannelTexture = raster.Texture;
                    _t0 = Lap(_sw, "compile.channels", ref _t0);
                }

                bool hasSkirt = settings.Skirt.Enabled;
                if (hasSkirt)
                {
                    renderData = MeshSkirtBuilder.Build(baseRenderSection, baseRenderWeights, settings.Skirt, Allocator.TempJob, out renderWeights);
                }
                else
                {
                    renderData = CopyMeshData(baseRenderSection, Allocator.TempJob);
                    renderWeights = CopyWeights(baseRenderWeights, baseRenderSection.VertexCount, Allocator.TempJob);
                }
                _t0 = Lap(_sw, "compile.skirt", ref _t0);

                compiled.RenderMeshes = BuildRenderLODs(renderData, renderWeights, settings, compiled);
                _t0 = Lap(_sw, "compile.lods", ref _t0);

                BuildRenderObjects(root.transform, compiled.RenderMeshes, settings);
                _t0 = Lap(_sw, "compile.renderObjects", ref _t0);

                if (raster != null)
                    foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>())
                        ChannelPacking.ApplyToRenderer(renderer, raster.Texture, raster.Table, raster.TexcoordMetrics);

                if (collisionMesh != null)
                {
                    compiled.CollisionMesh = collisionMesh;
                    compiled.Own(collisionMesh);
                    root.AddComponent<MeshCollider>().sharedMesh = collisionMesh;
                    _t0 = Lap(_sw, "compile.collider", ref _t0);
                }
            }
            finally
            {
                if (channelWeights != null) channelWeights.Dispose();
                if (channelSection.Vertices.IsCreated) channelSection.Dispose();
                if (renderWeights != null) renderWeights.Dispose();
                if (renderData.Vertices.IsCreated) renderData.Dispose();
                localSection.Dispose();
            }

            return compiled;
        }

        /// <summary>
        /// Thread-safe skirt build (a public entry to the internal builder) so the cook can apply the skirt on
        /// a worker thread before baking LODs. Pure <see cref="MeshData"/> work; caller owns the result.
        /// </summary>
        public static MeshData BuildSkirt(in MeshData source, WeightLayerSet weights, MeshSkirtSettings settings,
            Allocator allocator, out WeightLayerSet resultWeights)
            => MeshSkirtBuilder.Build(source, weights, settings, allocator, out resultWeights);

        /// <summary>
        /// Upload-only compile from <b>prebaked</b> LODs (skirt + simplification already done on the cook's
        /// worker thread). This is the streaming fast path: it does no skirt/simplify on the main thread —
        /// only mesh upload + component creation + collider — so the present cost is the cheap part the
        /// profiler measured (doc/08 §8). <paramref name="collisionSource"/> is the unskirted/unsimplified
        /// cooked mesh.
        /// </summary>
        public static CompiledSection CompilePrebaked(
            Streaming.LodMesh[] lods,
            in MeshData collisionSource,
            in GridDimensions dims,
            int3 coord,
            SectionCompilationSettings settings,
            Transform parent = null)
        {
            settings ??= new SectionCompilationSettings();
            if (lods == null || lods.Length == 0)
                throw new ArgumentException("CompilePrebaked requires at least one baked LOD.");

            var _sw = System.Diagnostics.Stopwatch.StartNew();
            double _t = 0;

            float3 origin = SectionOrigin(dims, coord);
            var compiled = new CompiledSection { Coord = coord };
            var root = new GameObject($"Section_{coord.x}_{coord.y}_{coord.z}") { hideFlags = HideFlags.DontSave };
            root.transform.SetParent(parent, false);
            root.transform.localPosition = (Vector3)origin;
            compiled.Root = root;
            _t = Lap(_sw, "pre.rootGO", ref _t);

            // Collision from the prebaked (cook-side, unskirted, simplified) collision mesh — upload + PhysX
            // bake only, no main-thread simplify. (doc/08 §8 perf pass.)
            if (settings.GenerateCollision && collisionSource.Vertices.IsCreated && collisionSource.TriangleCount > 0)
            {
                MeshData localCollision = CopyWithOffset(collisionSource, origin, Allocator.TempJob);
                try
                {
                    var collisionMesh = MeshDataConversions.ToCollisionMesh(localCollision, root.name + "_Collision");
                    compiled.CollisionMesh = collisionMesh;
                    compiled.Own(collisionMesh);
                    _t = Lap(_sw, "pre.collisionMesh", ref _t);
                    root.AddComponent<MeshCollider>().sharedMesh = collisionMesh;
                    _t = Lap(_sw, "pre.colliderBake", ref _t);
                }
                finally { localCollision.Dispose(); }
            }

            // Upload each prebaked LOD (offsetting verts to section-local; packing weights into UV2-7).
            var meshes = new Mesh[lods.Length];
            for (int i = 0; i < lods.Length; i++)
            {
                MeshData local = CopyWithOffset(lods[i].Mesh, origin, Allocator.TempJob);
                try
                {
                    Mesh m = MeshDataConversions.ToRenderMesh(local, $"LOD{i}");
                    _t = Lap(_sw, "pre.uploadMesh", ref _t);
                    PackWeightLayers(m, lods[i].Weights);
                    _t = Lap(_sw, "pre.packWeights", ref _t);
                    compiled.Own(m);
                    meshes[i] = m;
                }
                finally { local.Dispose(); }
            }
            compiled.RenderMeshes = meshes;
            _t = Lap(_sw, "pre.lodCopy+dispose", ref _t);

            BuildRenderObjects(root.transform, meshes, settings);
            _t = Lap(_sw, "pre.renderObjects", ref _t);

            return compiled;
        }

        /// <summary>Records the time since the previous lap point into <paramref name="phase"/> and returns the
        /// new cumulative elapsed. No-op cost beyond a stopwatch read when profiling is disabled.</summary>
        static double Lap(System.Diagnostics.Stopwatch sw, string phase, ref double prevTotal)
        {
            double now = sw.Elapsed.TotalMilliseconds;
            Streaming.StreamingProfiler.AddPhase(phase, now - prevTotal);
            return now;
        }

        public static float3 SectionOrigin(in GridDimensions dims, int3 absoluteCoord)
        {
            int3 local = absoluteCoord - dims.OriginCoord;
            return dims.CellMin(local);
        }

        static Mesh[] BuildRenderLODs(
            in MeshData renderData,
            WeightLayerSet weights,
            SectionCompilationSettings settings,
            CompiledSection owner)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Mesh lod0 = MeshDataConversions.ToRenderMesh(renderData, "LOD0");
            PackWeightLayers(lod0, weights);
            owner.Own(lod0);
            Streaming.StreamingProfiler.AddPhase("lods.upload+pack", sw.Elapsed.TotalMilliseconds); sw.Restart();

            float[] qualities = settings.GenerateLODs && settings.LODQualities != null && settings.LODQualities.Length > 0
                ? settings.LODQualities
                : new[] { 1f };
            var meshes = new Mesh[qualities.Length];
            meshes[0] = lod0;

            for (int i = 1; i < qualities.Length; i++)
            {
                float quality = math.clamp(qualities[i], 0.01f, 1f);
                Mesh simplified = Simplify(lod0, quality, $"LOD{i}");
                meshes[i] = simplified;
                owner.Own(simplified);
            }
            Streaming.StreamingProfiler.AddPhase("lods.simplify", sw.Elapsed.TotalMilliseconds);

            return meshes;
        }

        static Mesh Simplify(Mesh source, float quality, string name)
        {
            var simplifier = new MeshSimplifier(source)
            {
                SimplificationOptions = new SimplificationOptions
                {
                    PreserveBorderEdges = true,
                    PreserveUVSeamEdges = true,
                    PreserveUVFoldoverEdges = true,
                    PreserveSurfaceCurvature = false,
                    EnableSmartLink = true,
                    VertexLinkDistance = 1e-6,
                    MaxIterationCount = 100,
                    Agressiveness = 7.0,
                    ManualUVComponentCount = false,
                    UVComponentCount = 4,
                },
            };
            simplifier.SimplifyMesh(quality);
            Mesh result = simplifier.ToMesh();
            result.name = name;
            result.RecalculateBounds();
            return result;
        }

        static void BuildRenderObjects(Transform root, Mesh[] meshes, SectionCompilationSettings settings)
        {
            var lodGroup = root.gameObject.AddComponent<LODGroup>();
            var lods = new LOD[meshes.Length];
            float[] heights = settings.LODScreenRelativeTransitionHeights ?? Array.Empty<float>();

            for (int i = 0; i < meshes.Length; i++)
            {
                var go = new GameObject($"LOD{i}") { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(root, false);
                go.AddComponent<MeshFilter>().sharedMesh = meshes[i];
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = settings.Material != null ? settings.Material : DefaultMaterial();

                float height = i < heights.Length ? heights[i] : math.pow(0.5f, i + 1);
                lods[i] = new LOD(math.clamp(height, 0.01f, 1f), new Renderer[] { renderer });
            }

            lodGroup.SetLODs(lods);
            lodGroup.RecalculateBounds();
        }

        // Fallback so a Definition with no Material set doesn't render as Unity's magenta error material.
        // Assign Definition.Material to control the surface; this is only a visible-not-broken default.
        static Material _defaultMaterial;
        static bool _warnedNoMaterial;
        static Material DefaultMaterial()
        {
            if (_defaultMaterial == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                _defaultMaterial = new Material(shader) { name = "MeshTerrain Default (no Definition.Material)" };
            }
            if (!_warnedNoMaterial)
            {
                _warnedNoMaterial = true;
                UnityEngine.Debug.LogWarning("MeshTerrain: the MeshPartitionDefinition has no Material assigned — " +
                    "using a default URP/Lit material. Assign Definition.Material to control the terrain surface.");
            }
            return _defaultMaterial;
        }

        static void PackWeightLayers(Mesh mesh, WeightLayerSet weights)
        {
            if (weights == null || weights.LayerCount == 0)
                return;

            ValidateWeights(weights);

            for (int group = 0; group < (weights.LayerCount + 3) / 4; group++)
            {
                var packed = new List<Vector4>(mesh.vertexCount);
                for (int v = 0; v < mesh.vertexCount; v++)
                {
                    var value = Vector4.zero;
                    for (int c = 0; c < 4; c++)
                    {
                        int layer = group * 4 + c;
                        if (layer >= weights.LayerCount) break;
                        value[c] = weights.GetLayerByIndex(layer)[v];
                    }
                    packed.Add(value);
                }
                mesh.SetUVs(group + 2, packed);
            }
        }

        static void ValidateWeights(WeightLayerSet weights)
        {
            if (weights != null && weights.LayerCount > MaxPackedWeightLayers)
                throw new InvalidOperationException($"Section compilation supports up to {MaxPackedWeightLayers} weight layers.");
        }

        static MeshData CopyWithOffset(in MeshData source, float3 offset, Allocator allocator)
        {
            var dst = MeshData.Allocate(source.VertexCount, source.TriangleCount, allocator,
                source.HasNormals, source.HasChannelUVs, source.HasSourceUV0, source.HasBaseIDs);
            for (int v = 0; v < source.VertexCount; v++)
            {
                dst.Vertices[v] = source.Vertices[v] - offset;
                if (source.HasNormals) dst.Normals[v] = source.Normals[v];
                if (source.HasChannelUVs) dst.ChannelUVs[v] = source.ChannelUVs[v];
                if (source.HasSourceUV0) dst.SourceUV0[v] = source.SourceUV0[v];
            }
            for (int t = 0; t < source.TriangleCount; t++)
            {
                dst.Triangles[t] = source.Triangles[t];
                if (source.HasBaseIDs) dst.BaseIDLayer[t] = source.BaseIDLayer[t];
            }
            return dst;
        }

        static MeshData CopyMeshData(in MeshData source, Allocator allocator)
            => CopyWithOffset(source, float3.zero, allocator);

        static WeightLayerSet CopyWeights(WeightLayerSet source, int vertexCount, Allocator allocator)
        {
            if (source == null || source.LayerCount == 0)
                return null;

            var result = new WeightLayerSet(allocator);
            for (int i = 0; i < source.LayerCount; i++)
            {
                string name = source.LayerNames[i];
                var src = source.GetLayerByIndex(i);
                var dst = result.InitializeLayer(name, vertexCount);
                int count = math.min(vertexCount, src.Length);
                for (int v = 0; v < count; v++) dst[v] = src[v];
            }
            return result;
        }

        sealed class EdgeInfo
        {
            public int A;
            public int B;
            public int Count;
            public int SourceTriangle;
        }

        static class MeshSkirtBuilder
        {
            public static MeshData Build(
                in MeshData source,
                WeightLayerSet weights,
                MeshSkirtSettings settings,
                Allocator allocator,
                out WeightLayerSet resultWeights)
            {
                var edges = FindBoundaryEdges(source, settings.VertexSnapTolerance);
                var loops = BuildLoops(edges);

                var keptLoops = new List<List<int>>();
                int newVertCount = 0;
                int newTriCount = 0;
                foreach (var loop in loops)
                {
                    float perimeter = LoopPerimeter(source, loop);
                    if (perimeter < settings.BoundaryMinPerimeter)
                        continue;
                    keptLoops.Add(loop);
                    newVertCount += loop.Count;
                    newTriCount += loop.Count * 2;
                }

                var dst = MeshData.Allocate(source.VertexCount + newVertCount, source.TriangleCount + newTriCount,
                    allocator, source.HasNormals, source.HasChannelUVs, source.HasSourceUV0, source.HasBaseIDs);
                CopyInto(source, dst);

                resultWeights = CopyWeights(weights, source.VertexCount + newVertCount, allocator);

                int nextVertex = source.VertexCount;
                int nextTri = source.TriangleCount;
                foreach (var loop in keptLoops)
                {
                    int count = loop.Count;
                    var skirtVids = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        int cur = loop[i];
                        int prev = loop[(i - 1 + count) % count];
                        int next = loop[(i + 1) % count];
                        int skirtVid = nextVertex++;
                        skirtVids[i] = skirtVid;

                        dst.Vertices[skirtVid] = ComputeSkirtPosition(source, cur, prev, next, settings);
                        if (source.HasNormals) dst.Normals[skirtVid] = source.Normals[cur];
                        if (source.HasChannelUVs) dst.ChannelUVs[skirtVid] = source.ChannelUVs[cur];
                        if (source.HasSourceUV0) dst.SourceUV0[skirtVid] = source.SourceUV0[cur];
                        CopyWeightVertex(weights, resultWeights, cur, skirtVid);
                    }

                    for (int i = 0; i < count; i++)
                    {
                        int j = (i + 1) % count;
                        dst.Triangles[nextTri] = new int3(loop[i], loop[j], skirtVids[i]);
                        if (source.HasBaseIDs) dst.BaseIDLayer[nextTri] = BaseIDForEdge(source, loop[i], loop[j], edges);
                        nextTri++;
                        dst.Triangles[nextTri] = new int3(loop[j], skirtVids[j], skirtVids[i]);
                        if (source.HasBaseIDs) dst.BaseIDLayer[nextTri] = BaseIDForEdge(source, loop[i], loop[j], edges);
                        nextTri++;
                    }
                }

                return dst;
            }

            static int BaseIDForEdge(in MeshData source, int a, int b, List<EdgeInfo> edges)
            {
                foreach (var edge in edges)
                {
                    if ((edge.A == a && edge.B == b) || (edge.A == b && edge.B == a))
                        return source.BaseIDLayer[edge.SourceTriangle];
                }
                return 0;
            }
            static List<EdgeInfo> FindBoundaryEdges(in MeshData source, float snapTolerance)
            {
                var map = new Dictionary<(int, int), EdgeInfo>();
                var weld = BuildWeldMap(source, snapTolerance);
                for (int t = 0; t < source.TriangleCount; t++)
                {
                    int3 tri = source.Triangles[t];
                    AddEdge(map, weld, tri.x, tri.y, t);
                    AddEdge(map, weld, tri.y, tri.z, t);
                    AddEdge(map, weld, tri.z, tri.x, t);
                }

                var result = new List<EdgeInfo>();
                foreach (var edge in map.Values)
                    if (edge.Count == 1)
                        result.Add(edge);
                return result;
            }

            static int[] BuildWeldMap(in MeshData source, float snapTolerance)
            {
                var weld = new int[source.VertexCount];
                for (int i = 0; i < weld.Length; i++) weld[i] = i;
                if (snapTolerance <= 0f) return weld;

                float inv = 1f / snapTolerance;
                var buckets = new Dictionary<int3, int>();
                for (int i = 0; i < source.VertexCount; i++)
                {
                    int3 key = (int3)math.round(source.Vertices[i] * inv);
                    if (buckets.TryGetValue(key, out int existing))
                        weld[i] = existing;
                    else
                        buckets.Add(key, i);
                }
                return weld;
            }

            static void AddEdge(Dictionary<(int, int), EdgeInfo> map, int[] weld, int a, int b, int sourceTri)
            {
                int wa = weld[a];
                int wb = weld[b];
                var key = wa < wb ? (wa, wb) : (wb, wa);
                if (map.TryGetValue(key, out var edge))
                {
                    edge.Count++;
                    return;
                }
                map.Add(key, new EdgeInfo { A = a, B = b, Count = 1, SourceTriangle = sourceTri });
            }

            static List<List<int>> BuildLoops(List<EdgeInfo> edges)
            {
                var nextByVertex = new Dictionary<int, List<int>>();
                foreach (var edge in edges)
                {
                    if (!nextByVertex.TryGetValue(edge.A, out var list))
                        nextByVertex.Add(edge.A, list = new List<int>());
                    list.Add(edge.B);
                }

                var used = new HashSet<(int, int)>();
                var loops = new List<List<int>>();
                foreach (var edge in edges)
                {
                    if (used.Contains((edge.A, edge.B))) continue;

                    var loop = new List<int> { edge.A };
                    int start = edge.A;
                    int cur = edge.B;
                    used.Add((edge.A, edge.B));

                    int guard = edges.Count + 1;
                    while (guard-- > 0 && cur != start)
                    {
                        loop.Add(cur);
                        if (!nextByVertex.TryGetValue(cur, out var candidates)) break;
                        int next = -1;
                        foreach (int candidate in candidates)
                        {
                            if (!used.Contains((cur, candidate)))
                            {
                                next = candidate;
                                break;
                            }
                        }
                        if (next < 0) break;
                        used.Add((cur, next));
                        cur = next;
                    }

                    if (loop.Count >= 3)
                        loops.Add(loop);
                }
                return loops;
            }

            static float LoopPerimeter(in MeshData source, List<int> loop)
            {
                float length = 0f;
                for (int i = 0; i < loop.Count; i++)
                    length += math.distance(source.Vertices[loop[i]], source.Vertices[loop[(i + 1) % loop.Count]]);
                return length;
            }

            static float3 ComputeSkirtPosition(in MeshData source, int cur, int prev, int next, MeshSkirtSettings settings)
            {
                float3 p = source.Vertices[cur];
                float3 normal = source.HasNormals ? source.Normals[cur] : new float3(0f, 1f, 0f);
                float3 prevDir = math.normalizesafe(p - source.Vertices[prev]);
                float3 nextDir = math.normalizesafe(source.Vertices[next] - p);
                float3 offA = math.normalizesafe(math.cross(normal, prevDir));
                float3 offB = math.normalizesafe(math.cross(normal, nextDir));
                float3 offset = math.normalizesafe(offA + offB);
                if (math.lengthsq(offset) < 1e-8f)
                    offset = math.normalizesafe(math.cross(normal, nextDir));

                float3 push = settings.PushMethod == MeshSkirtPushMethod.VertexNormal
                    ? -normal
                    : new float3(0f, -1f, 0f);
                return p + offset * settings.Width + push * settings.PushDown;
            }

            static void CopyInto(in MeshData source, MeshData dst)
            {
                for (int v = 0; v < source.VertexCount; v++)
                {
                    dst.Vertices[v] = source.Vertices[v];
                    if (source.HasNormals) dst.Normals[v] = source.Normals[v];
                    if (source.HasChannelUVs) dst.ChannelUVs[v] = source.ChannelUVs[v];
                    if (source.HasSourceUV0) dst.SourceUV0[v] = source.SourceUV0[v];
                }
                for (int t = 0; t < source.TriangleCount; t++)
                {
                    dst.Triangles[t] = source.Triangles[t];
                    if (source.HasBaseIDs) dst.BaseIDLayer[t] = source.BaseIDLayer[t];
                }
            }

            static void CopyWeightVertex(WeightLayerSet source, WeightLayerSet dst, int sourceVid, int dstVid)
            {
                if (source == null || dst == null) return;
                for (int i = 0; i < source.LayerCount; i++)
                {
                    var srcLayer = source.GetLayerByIndex(i);
                    var dstLayer = dst.GetLayerByIndex(i);
                    dstLayer[dstVid] = srcLayer[sourceVid];
                }
            }
        }
    }
}
