using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Fca.MeshTerrain.Streaming
{
    /// <summary>
    /// Distance-driven per-section streamer (<c>doc/08_STREAMING_SYSTEM_DESIGN.md §3, §5, §8, §9</c>): each
    /// tick it computes the desired resident set around <see cref="Focus"/>, diffs it against what is
    /// resident, and enqueues loads/unloads under a per-frame budget. Loads resolve through the
    /// <see cref="SectionCache"/> (a hit avoids the cook) and present via an <see cref="ISectionPresenter"/>.
    ///
    /// <para>In steady state (focus moving inside an already-cooked world) this only ever instantiates —
    /// the heavy cook runs once per (cell, params) on a cache miss. Eviction frees all three memory systems
    /// (native arrays, UnityEngine.Objects, managed refs); the leak-free contract is guarded by a test.</para>
    ///
    /// <para>Phase 5.3: the modifier stack is supplied in code via <see cref="SetModifierStack"/>; cooking is
    /// synchronous under a count budget (async Burst/GPU cooking is a later optimization). Editor/MonoBehaviour
    /// modifier authoring is Phase 6.</para>
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Mesh Terrain/Mesh Terrain Streamer")]
    public sealed class MeshTerrainStreamer : MonoBehaviour
    {
        [Header("World")]
        public MeshPartitionDefinition Definition;
        public Transform Focus;
        [Tooltip("Anchor (world origin) of the streaming grid. Kept fixed for stable cell coords / cache.")]
        public Vector3 WorldOriginOffset = Vector3.zero;
        [Tooltip("Y extent of a 2D-terrain cell (must contain the tallest displacement).")]
        public float WorldHeight = 4000f;

        [Header("Streaming radii (world units)")]
        public float LoadDistance = 600f;
        public float UnloadDistance = 800f;

        [Header("Budget")]
        [Tooltip("Max cooks running concurrently on worker threads.")]
        public int MaxConcurrentCooks = 4;
        [Tooltip("Hard cap on sections PRESENTED per frame. Each present uploads meshes + bakes a collider " +
                 "(main-thread Unity calls that can't be hidden), so cap this low to keep frames smooth — " +
                 "sections then trickle in over a few frames.")]
        public int MaxPresentsPerFrame = 1;
        [Tooltip("Additional main-thread time budget per frame for finalizing presents (ms). Stops early if " +
                 "exceeded even before MaxPresentsPerFrame is reached.")]
        public float MaxMillisPerFrame = 6f;
        [Tooltip("Re-evaluate the desired set only when the focus moves at least this far (world units).")]
        public float RefocusThreshold = 25f;

        [Header("Channels")]
        public bool GenerateChannels = true;
        [Tooltip("Force a fixed channel-atlas resolution (>0) so all section atlases fit one shared " +
                 "Texture2DArray — required by the instanced (BRG shared-atlas) presenter. 0 = area-adaptive.")]
        public int FixedAtlasResolution = 0;

        [Header("Cache")]
        public int RamCapacity = 128;
        [Tooltip("Optional override for the cache directory (else persistentDataPath/MeshTerrain/<id>).")]
        public string CacheDirOverride = "";

        [Header("Modifiers")]
        [Tooltip("Assemble the modifier stack from child ModifierBehaviour components (UE-style scene " +
                 "authoring). When off, the stack is supplied in code via SetModifierStack (tests/demo).")]
        public bool UseSceneModifiers = true;

        // --- runtime state ---
        GridSettings _grid;
        GridDimensions _dims;
        StreamingPolicy _policy;
        ChannelCookOptions _channelOpts;
        LodCookOptions _lodOptions;
        SectionCompilationSettings _compileSettings;
        SectionCache _cache;
        ISectionPresenter _presenter;
        readonly List<ModifierComponent> _stack = new();
        // True once SetModifierStack supplied a code stack — that overrides scene collection (tests/demo).
        bool _codeStackOverride;
        // Scene wrappers backing the current scene-assembled stack (parallel to _stack); empty for a code stack.
        readonly List<ModifierBehaviour> _sceneWrappers = new();

        readonly Dictionary<int3, ResidentSection> _resident = new();
        readonly HashSet<int3> _desired = new();
        readonly List<int3> _loadQueue = new();
        readonly List<int3> _unloadScratch = new();

        // In-flight async cooks: coord -> pending cook. The cook runs on a worker thread; the result is
        // finalized (presented) on the main thread under the per-frame time budget.
        sealed class PendingLoad
        {
            public int3 Coord;
            public SectionKey Key;
            public Task<CookedSection> Task;
            public bool WasHit;        // true = came from cache (already cached; don't Put or dispose on cancel)
            public int Generation;
            public double CookThreadMs; // worker wall time of the cook (0 for a cache hit)
        }
        readonly Dictionary<int3, PendingLoad> _pending = new();
        readonly List<int3> _completedScratch = new();
        int _generation;

        bool _initialized;
        bool _hasLastFocus;
        float3 _lastFocus;
        float _cellMargin;
        double _lastFinalizeMs;
        int _lastFinalizedCount;

        public int residentCount => _resident.Count;
        public int pendingCount => _pending.Count;
        public int queuedCount => _loadQueue.Count;
        public double lastFinalizeMs => _lastFinalizeMs;
        public int lastFinalizedCount => _lastFinalizedCount;
        public ISectionCache Cache => _cache;
        /// <summary>The assembled modifier stack in applied order (read-only; diagnostics / tests).</summary>
        public IReadOnlyList<ModifierComponent> ModifierStack => _stack;

        /// <summary>A full profiler dump (phase table + slowest recent sections). Requires
        /// <see cref="StreamingProfiler.Enabled"/>. Hook this to a key/button to copy out the analytics.</summary>
        public string ProfilerReport() => StreamingProfiler.Report();

        /// <summary>Test helper: pump <see cref="Tick"/> until all in-flight cooks have been finalized (or a
        /// safety cap is hit). Uses a generous finalize budget so everything completes in-bounds.</summary>
        public void PumpUntilIdleForTest(float3 focusWorld, int maxTicks = 1000)
        {
            float saved = MaxMillisPerFrame;
            MaxMillisPerFrame = 1000f;
            int ticks = 0;
            do
            {
                Tick(focusWorld);
                if (_pending.Count > 0) System.Threading.Thread.Sleep(1); // let worker cooks finish
            }
            while ((_pending.Count > 0 || _loadQueue.Count > 0) && ++ticks < maxTicks);
            MaxMillisPerFrame = saved;
        }

        /// <summary>Supplies the modifier stack to stream (code-driven in Phase 5). Triggers a reset so the
        /// next tick re-resolves residency against the new stack.</summary>
        public void SetModifierStack(IReadOnlyList<ModifierComponent> stack)
        {
            _stack.Clear();
            _sceneWrappers.Clear();
            if (stack != null) _stack.AddRange(stack);
            _codeStackOverride = true; // a code stack wins over scene collection until cleared
            if (_initialized) ForceUnloadAll();
        }

        /// <summary>Overrides the default <see cref="GameObjectSectionPresenter"/> (e.g. for tests).</summary>
        public void SetPresenter(ISectionPresenter presenter) => _presenter = presenter;

        void OnEnable()
        {
            EnsureInitialized();
#if UNITY_EDITOR
            // In edit mode, MonoBehaviour.Update is unreliable (fires only on changes), so drive the streamer
            // from the editor update loop — full streaming (cook/cache/present + BRG render) without Play.
            if (!Application.isPlaying)
            {
                UnityEditor.EditorApplication.update += EditorTick;
                UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            }
#endif
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
#endif
            Teardown();
        }

#if UNITY_EDITOR
        // Tear down BEFORE a domain reload so native memory / BRG / GraphicsBuffers don't leak across reloads.
        void OnBeforeAssemblyReload() => Teardown();

        void EditorTick()
        {
            if (Application.isPlaying || this == null) return;
            // In edit mode the streaming focus is the SCENE-VIEW camera (what the user is looking at), not the
            // play-mode Focus transform: residency, cook-cancellation, and finalize must all agree with the view.
            // Using a Focus pointing at the Main Camera (often elsewhere) makes ShouldKeep reject finished cooks
            // for tiles visible in the Scene view → they get treated as "cancelled" and never presented (holes).
            var sv = UnityEditor.SceneView.lastActiveSceneView;
            Transform focus = (sv != null && sv.camera != null) ? sv.camera.transform : Focus;
            if (focus == null) return;
            Tick(focus.position);
            // While work is in flight, keep the editor actively ticking — EditorApplication.update is throttled
            // when the editor is idle (no input / unfocused window), so FinalizeCompleted would stop being called
            // with cooks still pending → permanently un-presented tiles (holes) until the next user action. Force
            // continued updates + a scene repaint so every completed cook drains this and the following frames.
            if (_pending.Count > 0 || _loadQueue.Count > 0)
            {
                UnityEditor.SceneView.RepaintAll();
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate(); // request another editor tick even when idle
            }
        }
#endif

        void EnsureInitialized()
        {
            if (_initialized) return;

            _grid = Definition != null
                ? GridSettings.FromDefinition(Definition, (float3)WorldOriginOffset)
                : new GridSettings { CellSize = 100f, Is2D = true, WorldOriginOffset = (float3)WorldOriginOffset };

            // Synthetic grid dimensions anchored at the world origin: OriginCoord = 0 so absolute coords map
            // directly through CellMin/CellExtent. The Y cell spans WorldHeight in 2D (must contain displacement).
            float yExtent = _grid.Is2D ? WorldHeight : _grid.CellSize;
            _dims = new GridDimensions
            {
                SnappedMin = _grid.WorldOriginOffset - new float3(0f, _grid.Is2D ? WorldHeight * 0.5f : 0f, 0f),
                OriginCoord = int3.zero,
                CellNumber = new int3(1, 1, 1),
                CellExtent = new float3(_grid.CellSize, yExtent, _grid.CellSize),
            };

            _policy = StreamingPolicy.FromDistances(_grid, LoadDistance, UnloadDistance);
            _channelOpts = ChannelCookOptions.FromDefinition(Definition, GenerateChannels);
            _channelOpts.FixedResolution = FixedAtlasResolution;
            // Global channel order so the shared atlas has slice i == global channel i for every section.
            if (Definition != null && Definition.ChannelNames != null && Definition.ChannelNames.Count > 0)
                _channelOpts.ChannelNames = Definition.ChannelNames.ToArray();

            _compileSettings = Definition != null
                ? SectionCompilationSettings.FromDefinition(Definition)
                : new SectionCompilationSettings();

            // Bake the skirt + LOD chain + collision in the cook (worker thread) so the present is upload-only.
            _lodOptions = new LodCookOptions
            {
                BakeLods = true,
                Qualities = _compileSettings.LODQualities,
                Skirt = _compileSettings.Skirt,
                BakeCollision = _compileSettings.GenerateCollision,
                CollisionQuality = _compileSettings.CollisionQuality,
            };
            _cellMargin = DefaultCellMargin();

            string id = Definition != null ? Definition.name : "default";
            string dir = string.IsNullOrEmpty(CacheDirOverride) ? null : CacheDirOverride;
            _cache = new SectionCache(id, RamCapacity, dir, Allocator.Persistent);

            _presenter ??= new GameObjectSectionPresenter(_compileSettings);

            _initialized = true;
            _hasLastFocus = false;

            // Assemble the stack from scene wrappers unless a code stack was supplied (tests/demo).
            if (UseSceneModifiers && !_codeStackOverride)
                CollectSceneModifiers();
        }

        /// <summary>
        /// The streaming grid's origin frame in world space. The cooker runs with <c>meshToWorld = identity</c>,
        /// so a modifier's mesh-local frame == this grid frame (anchored at <see cref="WorldOriginOffset"/>,
        /// oriented/scaled by this streamer's transform). Scene wrappers express their placement relative to it.
        /// </summary>
        float4x4 GridToWorld()
            => math.mul((float4x4)transform.localToWorldMatrix,
                        float4x4.Translate((float3)WorldOriginOffset));

        /// <summary>
        /// Rebuilds <see cref="_stack"/> from child <see cref="ModifierBehaviour"/>s, sorted base-first then by
        /// priority layer / sub-priority / sibling index (UE base-first + type/sub-priority + path tiebreak).
        /// No-op when a code stack overrides (tests/demo) or scene authoring is disabled.
        /// </summary>
        public void CollectSceneModifiers()
        {
            if (_codeStackOverride || !UseSceneModifiers) return;

            _sceneWrappers.Clear();
            GetComponentsInChildren<ModifierBehaviour>(true, _sceneWrappers); // includeInactive: disabled GOs still author

            // Deterministic apply order: bases first, then PriorityLayer, then SubPriority, then sibling order.
            _sceneWrappers.Sort((a, b) =>
            {
                if (a.IsBaseModifier != b.IsBaseModifier) return a.IsBaseModifier ? -1 : 1;
                int p = a.PriorityLayer.CompareTo(b.PriorityLayer);
                if (p != 0) return p;
                int s = a.SubPriority.CompareTo(b.SubPriority);
                if (s != 0) return s;
                return a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex());
            });

            float4x4 gridToWorld = GridToWorld();
            _stack.Clear();
            foreach (var w in _sceneWrappers)
            {
                if (w == null) continue;
                w.MarkDirty(); // pick up any transform/field change since the last build
                _stack.Add(w.GetCore(gridToWorld));
            }

            RebuildChannelNames();
        }

        // Builds the global channel list = Definition.ChannelNames (declared order) unioned with the channels
        // the scene modifiers write. Without this, a modifier writing a channel not declared in the Definition
        // gets no stable global atlas slot, so the shared atlas scatters it across tiles (the cook normalizes to
        // ChannelNames order — see SectionCooker.NormalizeToGlobalChannels). Cap at 24 (atlas slot budget).
        readonly List<string> _channelNameScratch = new();
        void RebuildChannelNames()
        {
            _channelNameScratch.Clear();
            if (Definition != null && Definition.ChannelNames != null)
                foreach (var n in Definition.ChannelNames)
                    if (!string.IsNullOrEmpty(n) && !_channelNameScratch.Contains(n)) _channelNameScratch.Add(n);

            foreach (var w in _sceneWrappers)
            {
                if (w == null) continue;
                w.GetWrittenChannels(_channelNameScratch);
            }
            // De-dup (wrappers may repeat names) and clamp to the 24-channel atlas budget.
            for (int i = _channelNameScratch.Count - 1; i >= 0; i--)
                if (string.IsNullOrEmpty(_channelNameScratch[i]) || _channelNameScratch.IndexOf(_channelNameScratch[i]) != i)
                    _channelNameScratch.RemoveAt(i);
            if (_channelNameScratch.Count > 24) _channelNameScratch.RemoveRange(24, _channelNameScratch.Count - 24);

            _channelOpts.ChannelNames = _channelNameScratch.Count > 0 ? _channelNameScratch.ToArray() : null;
        }

        void Update()
        {
            // Edit mode is driven by EditorTick (Update is unreliable there); avoid double-ticking.
            if (!Application.isPlaying) return;
            if (!_initialized || _stack.Count == 0) return;
            Transform focus = Focus != null ? Focus : (Camera.main != null ? Camera.main.transform : null);
            if (focus == null) return;
            Tick(focus.position);
        }

        /// <summary>Runs one streaming tick against an explicit focus. Exposed so tests can drive the async
        /// path deterministically without Unity's frame loop.</summary>
        public void Tick(float3 focusWorld)
        {
            if (!_initialized || _stack.Count == 0) return;

            // Re-resolve residency when the focus moved enough (or first frame), OR when nothing is in flight —
            // the idle re-resolve is a cheap safety net that re-queues any desired cell that was evicted or had
            // its cook cancelled (e.g. focus drift during a cook) and never re-queued, so holes can't persist.
            bool idle = _pending.Count == 0 && _loadQueue.Count == 0;
            if (!_hasLastFocus || math.distance(focusWorld, _lastFocus) >= RefocusThreshold || idle)
            {
                ResolveResidency(focusWorld);
                _lastFocus = focusWorld;
                _hasLastFocus = true;
            }

            // Start cooks (async, off the main thread) up to the concurrency cap.
            StartCooks(focusWorld);

            // Finalize completed cooks on the main thread, time-boxed so a frame never stalls.
            FinalizeCompleted(focusWorld);
        }

        void ResolveResidency(float3 focusWorld)
        {
            _policy.ComputeDesired(focusWorld, _desired);

            // Unload: resident coords outside the unload radius.
            _unloadScratch.Clear();
            foreach (var kvp in _resident)
                if (!_policy.ShouldKeep(kvp.Key, focusWorld))
                    _unloadScratch.Add(kvp.Key);
            foreach (var coord in _unloadScratch)
                Evict(coord);

            // Load: desired coords not yet resident (and not already queued).
            foreach (var coord in _desired)
                if (!_resident.ContainsKey(coord) && !_loadQueue.Contains(coord))
                    _loadQueue.Add(coord);

            // Closest-first so nearby sections appear before distant ones.
            int3 center = _policy.FocusCell(focusWorld);
            _loadQueue.Sort((a, b) => _policy.RingDistance(a, center).CompareTo(_policy.RingDistance(b, center)));
        }

        /// <summary>Starts cooks for queued coords up to the concurrency cap. A cache hit is resolved
        /// synchronously (cheap), a miss is launched on a worker thread (<c>doc/08 §8</c>).</summary>
        void StartCooks(float3 focusWorld)
        {
            int i = 0;
            while (i < _loadQueue.Count && _pending.Count < math.max(1, MaxConcurrentCooks))
            {
                int3 coord = _loadQueue[i];

                // Cancel a queued load that drifted out of range or is already handled.
                if (!_policy.ShouldKeep(coord, focusWorld) || _resident.ContainsKey(coord) || _pending.ContainsKey(coord))
                {
                    _loadQueue.RemoveAt(i);
                    continue;
                }

                var swKey = Stopwatch.StartNew();
                SectionKey key = SectionKeyBuilder.Build(_stack, _grid, _dims, coord, _cellMargin, _channelOpts, _lodOptions);
                StreamingProfiler.AddPhase("key.build", swKey.Elapsed.TotalMilliseconds);

                Task<CookedSection> task;
                bool wasHit;
                var pendingLoad = new PendingLoad { Coord = coord, Key = key, Generation = _generation };
                if (_cache.TryGet(key, out var hit))
                {
                    // CACHE HIT: a previously baked tile (RAM or disk). No pipeline work — instantiate only.
                    StreamingDiagnostics.CacheHits++;
                    task = Task.FromResult(hit);
                    wasHit = true;
                }
                else
                {
                    // CACHE MISS: run the full cook pipeline once on a worker thread.
                    StreamingDiagnostics.CacheMisses++;
                    StreamingDiagnostics.Cooks++;

                    // Snapshot the stack so concurrent cooks read a stable list (params are immutable per cook).
                    var stack = _stack.ToArray();
                    var grid = _grid; var dims = _dims; int3 c = coord;
                    float margin = _cellMargin; var channels = _channelOpts; var lod = _lodOptions;
                    var pl = pendingLoad;
                    task = Task.Run(() =>
                    {
                        var sw = Stopwatch.StartNew();
                        var result = SectionCooker.Cook(stack, grid, dims, c, margin, channels, lod, float4x4.identity, Allocator.Persistent);
                        pl.CookThreadMs = sw.Elapsed.TotalMilliseconds;
                        return result;
                    });
                    wasHit = false;
                }

                pendingLoad.Task = task;
                pendingLoad.WasHit = wasHit;
                _pending[coord] = pendingLoad;
                _resident[coord] = new ResidentSection { Coord = coord, State = SectionState.Cooking, Generation = _generation };
                _loadQueue.RemoveAt(i);
            }
        }

        /// <summary>Finalizes (presents) cooks that have completed, on the main thread, until the per-frame
        /// time budget is spent. Present touches Unity objects (mesh upload, AddComponent, PhysX collider).</summary>
        void FinalizeCompleted(float3 focusWorld)
        {
            if (_pending.Count == 0) return;

            var sw = Stopwatch.StartNew();
            float budgetMs = math.max(0.5f, MaxMillisPerFrame);

            _completedScratch.Clear();
            foreach (var kvp in _pending)
                if (kvp.Value.Task.IsCompleted)
                    _completedScratch.Add(kvp.Key);

            int finalizedThisFrame = 0;
            int presentCap = math.max(1, MaxPresentsPerFrame);
            foreach (var coord in _completedScratch)
            {
                // Two caps: a hard count of heavy presents (mesh upload + collider) and a time budget.
                if (finalizedThisFrame >= presentCap)
                {
                    StreamingProfiler.AddPhase("finalize.presentCapHit", 1);
                    break;
                }
                if (sw.Elapsed.TotalMilliseconds >= budgetMs)
                {
                    StreamingProfiler.AddPhase("finalize.budgetHit", 1); // count = how often the budget capped us
                    break;   // resume next frame
                }

                var pending = _pending[coord];
                _pending.Remove(coord);

                CookedSection cooked;
                try { cooked = pending.Task.Result; }
                catch (System.Exception e)
                {
                    Debug.LogError($"MeshTerrainStreamer: cook failed for {coord}: {e}");
                    _resident.Remove(coord);
                    continue;
                }

                // Cancelled while cooking (drifted out of range, or unloaded): don't present, but the cook is
                // already done — cache a fresh result so the cell isn't re-cooked when it returns (the churn
                // that showed up as many cooks for few live sections). Then release RAM (keep the disk blob).
                bool stillWanted = _resident.TryGetValue(coord, out var r) && r.State == SectionState.Cooking
                                   && _policy.ShouldKeep(coord, focusWorld);
                if (!stillWanted)
                {
                    StreamingDiagnostics.CooksCancelled++;
                    if (pending.WasHit)
                    {
                        _cache.OnEvicted(coord);                  // cache owns it; release RAM, keep disk
                    }
                    else if (cooked.Mesh.TriangleCount > 0)
                    {
                        _cache.Put(pending.Key, cooked);          // cache the finished cook, then free RAM
                        _cache.OnEvicted(coord);
                    }
                    else
                    {
                        cooked.Dispose();                         // empty fresh cook: nothing to cache
                    }
                    _resident.Remove(coord);
                    continue;
                }

                // Empty cell: record residency (so we don't re-cook every refocus) without presenting/caching.
                if (cooked.Mesh.TriangleCount == 0)
                {
                    if (pending.WasHit) _cache.OnEvicted(coord);
                    else cooked.Dispose();
                    r.State = SectionState.Ready;
                    r.Handle = null;
                    continue;
                }

                // Cache a fresh cook (a hit is already cached) then present.
                if (!pending.WasHit)
                {
                    var swPut = Stopwatch.StartNew();
                    _cache.Put(pending.Key, cooked);
                    StreamingProfiler.AddPhase("cache.put", swPut.Elapsed.TotalMilliseconds);
                }

                var swPresent = Stopwatch.StartNew();
                ISectionHandle handle = _presenter.Present(cooked, transform);
                double presentMs = swPresent.Elapsed.TotalMilliseconds;
                StreamingProfiler.AddPhase("present.total", presentMs);

                StreamingProfiler.AddSample(new StreamingProfiler.SectionSample
                {
                    CoordX = coord.x, CoordY = coord.y, CoordZ = coord.z,
                    Vertices = cooked.Mesh.VertexCount, Triangles = cooked.Mesh.TriangleCount,
                    Lods = _compileSettings != null && _compileSettings.LODQualities != null ? _compileSettings.LODQualities.Length : 1,
                    WasCacheHit = pending.WasHit,
                    CookThreadMs = pending.CookThreadMs,
                    PresentMs = presentMs,
                });

                StreamingDiagnostics.LiveSections++;
                r.State = SectionState.Ready;
                r.Handle = handle;
                finalizedThisFrame++;
            }

            if (finalizedThisFrame > 0)
            {
                StreamingProfiler.AddPhase("finalize.frame", sw.Elapsed.TotalMilliseconds);
                _lastFinalizeMs = sw.Elapsed.TotalMilliseconds;
                _lastFinalizedCount = finalizedThisFrame;
            }
        }

        void Evict(int3 coord)
        {
            if (!_resident.TryGetValue(coord, out var r)) return;
            _resident.Remove(coord);                 // (c) drop the managed ref

            // If a cook is still in flight for this coord, it stays in _pending and is disposed when it
            // completes (FinalizeCompleted sees no resident entry -> drops it). The state flag prevents present.
            if (r.State == SectionState.Cooking)
                return;

            if (r.Handle != null)
            {
                _presenter.Release(r.Handle);        // (b) Destroy GO + meshes + atlas texture
                StreamingDiagnostics.LiveSections--;
            }
            _cache.OnEvicted(coord);                 // (a) free RAM native arrays; disk blob kept
        }

        /// <summary>Unloads every resident section (draining any in-flight cooks). Disk cache is preserved.</summary>
        public void ForceUnloadAll()
        {
            if (!_initialized) return;
            _loadQueue.Clear();
            DrainPending();                          // finish + dispose in-flight cooks so nothing leaks
            _unloadScratch.Clear();
            _unloadScratch.AddRange(_resident.Keys);
            foreach (var coord in _unloadScratch)
                Evict(coord);
            _unloadScratch.Clear();
            _hasLastFocus = false;
        }

        /// <summary>Forces a single cell to load now, synchronously (bypasses the async path + budget). For
        /// tooling/tests that need the section present immediately on return.</summary>
        public void ForceLoad(int3 coord)
        {
            EnsureInitialized();
            if (_resident.ContainsKey(coord)) return;

            SectionKey key = SectionKeyBuilder.Build(_stack, _grid, _dims, coord, _cellMargin, _channelOpts, _lodOptions);

            CookedSection cooked;
            bool wasHit;
            if (_cache.TryGet(key, out cooked))
            {
                StreamingDiagnostics.CacheHits++;
                wasHit = true;
            }
            else
            {
                StreamingDiagnostics.CacheMisses++;
                StreamingDiagnostics.Cooks++;
                cooked = SectionCooker.Cook(_stack, _grid, _dims, coord, _cellMargin, _channelOpts, _lodOptions, float4x4.identity, Allocator.Persistent);
                wasHit = false;
            }

            if (cooked.Mesh.TriangleCount == 0)
            {
                if (wasHit) _cache.OnEvicted(coord); else cooked.Dispose();
                _resident[coord] = new ResidentSection { Coord = coord, State = SectionState.Ready, Handle = null };
                return;
            }

            if (!wasHit) _cache.Put(key, cooked);
            ISectionHandle handle = _presenter.Present(cooked, transform);
            StreamingDiagnostics.LiveSections++;
            _resident[coord] = new ResidentSection { Coord = coord, State = SectionState.Ready, Handle = handle };
        }

        /// <summary>Blocks until every in-flight cook completes, disposing each result (none are presented).
        /// Used on unload/teardown so a worker thread never writes into freed native memory.</summary>
        void DrainPending()
        {
            if (_pending.Count == 0) return;
            foreach (var kvp in _pending)
            {
                try
                {
                    var cooked = kvp.Value.Task.Result;   // waits for completion
                    if (kvp.Value.WasHit) _cache.OnEvicted(kvp.Key);
                    else cooked.Dispose();
                }
                catch (System.Exception e) { Debug.LogError($"MeshTerrainStreamer: drained cook failed for {kvp.Key}: {e}"); }
            }
            _pending.Clear();
        }

        /// <summary>Invalidates the cells a modifier covers so they re-cook on next load (Phase 5.4 hook).</summary>
        public void InvalidateModifier(ModifierComponent m)
        {
            if (!_initialized || m == null) return;
            Bounds b = m.ComputeBounds();
            _unloadScratch.Clear();
            foreach (var coord in _resident.Keys)
            {
                Bounds cell = CellWorldBounds(coord);
                if (cell.Intersects(b)) _unloadScratch.Add(coord);
            }
            foreach (var coord in _unloadScratch)
                Evict(coord);              // next refocus re-loads with the new key -> miss -> re-cook
            _unloadScratch.Clear();
            _hasLastFocus = false;         // force a re-resolve next tick
        }

        /// <summary>
        /// A single scene modifier's params/transform changed: re-cook only the cells it covered (old footprint)
        /// and now covers (new footprint). Mirrors UE <c>PostEditChangeProperty</c>/<c>PostEditComponentMove</c>
        /// → <c>OnChanged(bounds)</c> — scoped, incremental. No-op for a code stack / disabled scene authoring.
        /// </summary>
        public void NotifyModifierEdited(ModifierBehaviour wrapper)
        {
            if (!_initialized || wrapper == null || _codeStackOverride || !UseSceneModifiers) return;

            // Membership change (added/removed since last collect) → full re-resolve.
            int idx = _sceneWrappers.IndexOf(wrapper);
            if (idx < 0 || idx >= _stack.Count) { NotifyModifierStackChanged(); return; }

            // Invalidate the OLD footprint from the core still in the stack (built at the previous collect, so it
            // reflects the pre-edit bounds even though OnValidate already marked the wrapper dirty).
            InvalidateModifier(_stack[idx]);

            // Re-collect (rebuilds dirty wrapper cores from new fields/transform), then invalidate the NEW footprint.
            CollectSceneModifiers();
            int newIdx = _sceneWrappers.IndexOf(wrapper);
            if (newIdx >= 0 && newIdx < _stack.Count)
                InvalidateModifier(_stack[newIdx]);
        }

        /// <summary>
        /// Stack membership or ordering changed (modifier added/removed/reordered/enable toggled). Re-collect
        /// and hard-reset residency so the whole world re-resolves against the new stack (matches
        /// <see cref="SetModifierStack"/> semantics). Cheap: presentation is cache-backed.
        /// </summary>
        public void NotifyModifierStackChanged()
        {
            if (!_initialized || _codeStackOverride || !UseSceneModifiers) return;
            CollectSceneModifiers();
            ForceUnloadAll();
            _hasLastFocus = false;
        }

        void Teardown()
        {
            if (!_initialized) return;
            ForceUnloadAll();
            _cache?.Dispose();
            _cache = null;
            _initialized = false;
        }

        // ---- helpers ----

        float DefaultCellMargin()
        {
            // Cover the skirt ribbon + a centroid-jitter margin (~ one max triangle edge). Derived from the
            // compile skirt width with a floor; conservative is safe (a slightly larger bounded build).
            float skirt = _compileSettings != null ? _compileSettings.Skirt.Width : 0f;
            return math.max(skirt, _grid.CellSize * 0.05f);
        }

        Bounds CellWorldBounds(int3 coord)
        {
            float3 min = _dims.CellMin(coord - _dims.OriginCoord);
            float3 max = min + _dims.CellExtent;
            var b = new Bounds();
            b.SetMinMax((Vector3)min, (Vector3)max);
            return b;
        }
    }
}
