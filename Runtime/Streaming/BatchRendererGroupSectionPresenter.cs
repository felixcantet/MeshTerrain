using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fca.MeshTerrain.Streaming
{
    /// <summary>
    /// GPU-driven section presenter (milestone 5.6a) drawing every resident section through a
    /// <see cref="BatchRendererGroup"/> — no GameObjects, no per-section MeshRenderer/LODGroup. This is the
    /// path that lifts the GameObject-presenter scaling ceiling (per-tile mesh upload + AddComponent + PhysX
    /// on the main thread). Added as a second <see cref="ISectionPresenter"/>; the cook/cache/streamer core is
    /// untouched (<c>doc/08 §4</c>).
    ///
    /// <para><b>Prototype scope (5.6a):</b> single LOD (LOD0), flat lit material (no channel atlas yet —
    /// 5.6c), no GPU LOD/cull (draws all resident, frustum-culled by Unity's batch culling defaults). Proves
    /// the instanced draw works and re-profiles vs the GameObject path. Collision is NOT handled here (BRG is
    /// render-only) — a separate collider path comes in 5.6d.</para>
    /// </summary>
    public sealed class BatchRendererGroupSectionPresenter : ISectionPresenter, IDisposable
    {
        // Per-instance data laid out for SRP DOTS instancing. The buffer is a Raw (float) buffer:
        //   [ 16f header ][ ObjectToWorld: 12f*cap ][ WorldToObject: 12f*cap ][ _ChannelParams: 4f*cap ]
        // Each matrix is a packed float3x4 (12 floats). _ChannelParams = (sliceBase, channelCount, _, _).
        // Metadata offsets below are in BYTES.
        const int HeaderFloats = 16;                 // 64-byte reserved header (instance 0)
        const int FloatsPerMatrix = 12;              // float3x4
        const int FloatsPerParams = 4;               // float4

        readonly BatchRendererGroup _brg;
        readonly Material _material;                  // base flat material (sections without channels)
        readonly BatchMaterialID _materialId;
        // One tinted material variant per LOD band for the LOD-debug view (materialID-selected, no DOTS prop).
        BatchMaterialID[] _lodMaterialIds;
        Material[] _lodMaterials;

        // Shared-atlas channel path: ONE material + ONE Texture2DArray for ALL sections, instanced via a
        // per-instance _ChannelParams (sliceBase, count). This is what makes draw calls stop scaling with
        // section count (vs one SetPass per section). The atlas requires a uniform cooked resolution.
        readonly Material _sharedChannelMaterial;
        readonly BatchMaterialID _sharedChannelMaterialId;
        readonly bool _hasSharedChannels;
        SharedChannelAtlas _atlas;
        readonly int _atlasResolution;
        readonly int _atlasCapacity;
        bool _diagLogged;

        // One batch over a growable instance buffer; meshes are registered per section.
        GraphicsBuffer _instanceBuffer;
        BatchID _batchId;
        bool _batchValid;
        int _capacity;

        sealed class Slot : ISectionHandle
        {
            public int3 Coord { get; set; }
            public int Index;            // instance index in the buffer (>=1; 0 is reserved)
            public Mesh[] LodMeshes;     // owned, LOD0 first
            public BatchMeshID[] LodIds;
            public float4x4 ObjectToWorld;
            public Bounds WorldBounds;
            public float3 Center;        // world center, for LOD distance
            public float Radius;         // world bounding radius, for screen-size LOD + cull

            // Shared-atlas channel slices: this section's channels occupy [SliceBase, SliceBase+ChannelCount)
            // in the shared Texture2DArray. HasChannels=false → drawn with the flat base material.
            public int SliceBase;
            public int ChannelCount;
            public bool HasChannels;
        }

        /// <summary>Draw each section with a per-LOD tinted material (LOD0 green → coarse red) so LOD
        /// selection is visible without wireframe (BRG draws don't show in Scene wireframe). Off = the shared
        /// channel material (or flat base material when a section has no channels).</summary>
        public bool DebugLodColors = false;

        readonly Dictionary<int3, Slot> _slots = new();
        readonly List<Slot> _live = new();              // dense list for draw emission
        readonly Transform _root;                        // parent for bookkeeping only (no per-section GO)

        // LOD switch distances in multiples of a section's bounding radius (LOD0 nearest). A section uses
        // LOD i once camera distance exceeds _lodDistanceFactors[i-1] * radius. Distance-based (not FOV) so it
        // doesn't depend on uncertain LODParameters fields and is predictable for uniform terrain tiles.
        float[] _lodDistanceFactors = { 4f, 10f };

        /// <param name="atlasResolution">Fixed shared-atlas slice resolution (must match the cook's
        /// FixedResolution). 0 disables the shared-channel path (flat material only).</param>
        /// <param name="atlasCapacity">Max slices in the shared atlas array.</param>
        public BatchRendererGroupSectionPresenter(Material material, Transform root = null,
            float[] lodTransitionHeights = null, int atlasResolution = 0, int atlasCapacity = 1024)
        {
            _material = material != null ? material : new Material(Shader.Find("Mesh Terrain/Instanced Flat (URP, BRG)"));
            _root = root;
            _atlasResolution = atlasResolution;
            _atlasCapacity = math.max(1, atlasCapacity);
            // Map LODGroup-style screen heights (high→low) to distance factors (near→far): a smaller screen
            // height means the LOD kicks in further away. Rough inverse mapping keeps demo parity.
            if (lodTransitionHeights != null && lodTransitionHeights.Length > 0)
            {
                _lodDistanceFactors = new float[lodTransitionHeights.Length];
                for (int i = 0; i < lodTransitionHeights.Length; i++)
                    _lodDistanceFactors[i] = math.max(1f, 2f / math.max(0.01f, lodTransitionHeights[i]));
            }

            _brg = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);
            _materialId = _brg.RegisterMaterial(_material);

            // Shared-atlas channel material + array (one for all sections → instanced batches).
            if (_atlasResolution > 0)
            {
                var sharedShader = Shader.Find("Mesh Terrain/Channel Blend Shared Atlas (URP, BRG)");
                if (sharedShader != null)
                {
                    _atlas = new SharedChannelAtlas(_atlasResolution, _atlasCapacity);
                    _sharedChannelMaterial = new Material(sharedShader) { name = "SharedChannels" };
                    _sharedChannelMaterial.SetTexture("_ChannelTex", _atlas.Texture);
                    _sharedChannelMaterialId = _brg.RegisterMaterial(_sharedChannelMaterial);
                    _hasSharedChannels = true;
                }
            }

            // Per-LOD tinted material variants (debug view). Each is a clone of the base with _LodColor set.
            _lodMaterials = new Material[LodDebugColors.Length];
            _lodMaterialIds = new BatchMaterialID[LodDebugColors.Length];
            for (int i = 0; i < LodDebugColors.Length; i++)
            {
                var m = new Material(_material) { name = $"LodTint{i}" };
                float4 c = LodDebugColors[i];
                m.SetVector("_LodColor", new Vector4(c.x, c.y, c.z, 1f));
                _lodMaterials[i] = m;
                _lodMaterialIds[i] = _brg.RegisterMaterial(m);
            }

            // Global bounds so the group is never culled wholesale; per-section bounds drive real culling.
            _brg.SetGlobalBounds(new Bounds(Vector3.zero, Vector3.one * 1_000_000f));

            EnsureCapacity(256);
        }

        // ---- ISectionPresenter ----

        public ISectionHandle Present(CookedSection cooked, Transform root)
        {
            // Upload every baked LOD (cooked vertices are already in WORLD space → identity transform). LOD is
            // chosen per-frame in OnPerformCulling by camera distance; the same identity instance is reused for
            // whichever LOD mesh is drawn.
            int lodCount = cooked.HasBakedLods ? cooked.Lods.Length : 1;
            var meshes = new Mesh[lodCount];
            var ids = new BatchMeshID[lodCount];
            Bounds bounds = default;
            for (int i = 0; i < lodCount; i++)
            {
                MeshData src = cooked.HasBakedLods ? cooked.Lods[i].Mesh : cooked.Mesh;
                Mesh m = MeshDataConversions.ToRenderMesh(src, $"BRG_{cooked.Coord.x}_{cooked.Coord.y}_{cooked.Coord.z}_L{i}");
                meshes[i] = m;
                ids[i] = _brg.RegisterMesh(m);
                if (i == 0) bounds = m.bounds;
            }

            var slot = new Slot
            {
                Coord = cooked.Coord,
                LodMeshes = meshes,
                LodIds = ids,
                ObjectToWorld = float4x4.identity,
                WorldBounds = bounds,
                Center = (float3)bounds.center,
                Radius = math.length((float3)bounds.extents),
            };

            // Shared-atlas channels: reserve a slice range and upload this section's R8 blob (channel c at
            // slice sliceBase+c). The per-instance _ChannelParams (sliceBase, count) is written below.
            if (cooked.HasAtlas && _hasSharedChannels && cooked.ChannelAtlasResolution == _atlasResolution)
            {
                int channelCount = math.max(1, cooked.ChannelAtlasSlices);
                int sliceBase = _atlas.Allocate(channelCount);
                if (sliceBase >= 0)
                {
                    _atlas.WriteSlices(sliceBase, cooked.ChannelAtlasBlob, cooked.ChannelAtlasResolution, channelCount);
                    slot.SliceBase = sliceBase;
                    slot.ChannelCount = channelCount;
                    slot.HasChannels = true;
                }
                else
                {
                    Debug.LogWarning($"SharedChannelAtlas full (cap {_atlasCapacity}); section {cooked.Coord} drawn flat.");
                }
            }
            else if (!_diagLogged)
            {
                // One-time diagnostic so a silent flat fallback is explainable.
                _diagLogged = true;
                Debug.LogWarning($"BRG channels OFF for {cooked.Coord}: HasAtlas={cooked.HasAtlas}, " +
                    $"sharedChannels={_hasSharedChannels}, cookedRes={cooked.ChannelAtlasResolution}, atlasRes={_atlasResolution}, " +
                    $"slices={cooked.ChannelAtlasSlices}. (atlasRes 0 = constructor got resolution 0; res mismatch = cook not fixed-res.)");
            }

            slot.Index = _live.Count + 1;     // instance 0 reserved
            _live.Add(slot);
            _slots[cooked.Coord] = slot;

            EnsureCapacity(_live.Count + 1);
            WriteInstance(slot.Index, slot.ObjectToWorld);
            WriteChannelParams(slot.Index, slot.HasChannels ? slot.SliceBase : 0, slot.HasChannels ? slot.ChannelCount : 0);

            return slot;
        }

        public void Release(ISectionHandle handle)
        {
            if (!(handle is Slot slot)) return;
            if (!_slots.Remove(slot.Coord)) return;

            // Swap-remove from the dense live list and rewrite the moved instance's transform.
            int removeAt = _live.IndexOf(slot);
            int lastIdx = _live.Count - 1;
            if (removeAt >= 0)
            {
                if (removeAt != lastIdx)
                {
                    Slot moved = _live[lastIdx];
                    _live[removeAt] = moved;
                    moved.Index = removeAt + 1;
                    WriteInstance(moved.Index, moved.ObjectToWorld);
                }
                _live.RemoveAt(lastIdx);
            }

            DestroyLods(slot);
        }

        void DestroyLods(Slot slot)
        {
            if (slot.LodMeshes != null)
            {
                for (int i = 0; i < slot.LodMeshes.Length; i++)
                {
                    _brg.UnregisterMesh(slot.LodIds[i]);
                    DestroyObj(slot.LodMeshes[i]);
                }
                slot.LodMeshes = null;
                slot.LodIds = null;
            }

            // Return the section's shared-atlas slice range to the allocator.
            if (slot.HasChannels && _atlas != null)
            {
                _atlas.Free(slot.SliceBase, slot.ChannelCount);
                slot.HasChannels = false;
            }
        }

        static void DestroyObj(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }

        // ---- BRG culling callback ----

        unsafe JobHandle OnPerformCulling(
            BatchRendererGroup rendererGroup, BatchCullingContext cullingContext,
            BatchCullingOutput cullingOutput, IntPtr userContext)
        {
            int count = _live.Count;
            if (count == 0 || !_batchValid)
            {
                cullingOutput.drawCommands[0] = default;
                return new JobHandle();
            }

            // Camera position for distance-based LOD + frustum planes for culling.
            float3 camPos = cullingContext.lodParameters.cameraPosition;
            var planes = cullingContext.cullingPlanes; // NativeArray<Plane>

            // First pass: frustum-cull + pick a LOD per visible section. Worst case = all visible.
            int* lodOf = Malloc<int>(count);          // chosen LOD per visible section
            int* slotOf = Malloc<int>(count);         // _live index per visible section
            int visible = 0;
            for (int i = 0; i < count; i++)
            {
                Slot s = _live[i];
                if (!FrustumIntersects(planes, s.Center, s.Radius)) continue;

                float dist = math.distance(camPos, s.Center);
                int lod = SelectLod(dist, s.Radius, s.LodMeshes.Length);

                lodOf[visible] = lod;
                slotOf[visible] = i;
                visible++;
            }

            var output = new BatchCullingOutputDrawCommands();
            if (visible == 0)
            {
                UnsafeUtility.Free(lodOf, Allocator.TempJob);
                UnsafeUtility.Free(slotOf, Allocator.TempJob);
                output.drawCommandCount = 0;
                cullingOutput.drawCommands[0] = output;
                return new JobHandle();
            }

            // One visible-instance + one draw command per visible section (each is its own mesh).
            output.visibleInstanceCount = visible;
            output.visibleInstances = Malloc<int>(visible);
            output.drawCommandCount = visible;
            output.drawCommands = Malloc<BatchDrawCommand>(visible);
            for (int i = 0; i < visible; i++)
            {
                Slot s = _live[slotOf[i]];
                int lod = lodOf[i];
                output.visibleInstances[i] = s.Index;
                // Material priority: LOD-debug tint (if on) > the SHARED channel material (one for all
                // channel sections → they batch) > the flat base material (sections without channels).
                BatchMaterialID mat = DebugLodColors
                    ? _lodMaterialIds[math.min(lod, _lodMaterialIds.Length - 1)]
                    : (s.HasChannels ? _sharedChannelMaterialId : _materialId);
                output.drawCommands[i] = new BatchDrawCommand
                {
                    visibleOffset = (uint)i,
                    visibleCount = 1,
                    batchID = _batchId,
                    materialID = mat,
                    meshID = s.LodIds[lod],
                    submeshIndex = 0,
                    splitVisibilityMask = 0xff,
                    flags = BatchDrawCommandFlags.None,
                    sortingPosition = 0,
                };
            }

            output.drawCommandPickingInstanceIDs = null;
            output.drawRangeCount = 1;
            output.drawRanges = Malloc<BatchDrawRange>(1);
            output.drawRanges[0] = new BatchDrawRange
            {
                drawCommandsBegin = 0,
                drawCommandsCount = (uint)visible,
                filterSettings = new BatchFilterSettings
                {
                    renderingLayerMask = 0xffffffff,
                    layer = 0,
                    motionMode = MotionVectorGenerationMode.Camera,
                    shadowCastingMode = ShadowCastingMode.On,
                    receiveShadows = true,
                    staticShadowCaster = false,
                    allDepthSorted = false,
                },
            };

            UnsafeUtility.Free(lodOf, Allocator.TempJob);
            UnsafeUtility.Free(slotOf, Allocator.TempJob);

            cullingOutput.drawCommands[0] = output;
            return new JobHandle();
        }

        /// <summary>Debug: force every section to LOD0 (disable LOD switching) — to isolate whether LOD
        /// simplification is corrupting channel UVs (broken paint arcs on far/coarse sections).</summary>
        public bool ForceLod0;

        /// <summary>Distance-based LOD: LOD i is used until camera distance exceeds factor[i]*radius, then the
        /// next coarser LOD; the last LOD is the fallthrough.</summary>
        int SelectLod(float distance, float radius, int lodCount)
        {
            if (ForceLod0) return 0;
            int last = lodCount - 1;
            for (int i = 0; i < last && i < _lodDistanceFactors.Length; i++)
                if (distance <= _lodDistanceFactors[i] * radius) return i;
            return last;
        }

        /// <summary>Sphere-vs-frustum test (section bounding sphere against the culling planes).</summary>
        static bool FrustumIntersects(NativeArray<Plane> planes, float3 center, float radius)
        {
            for (int p = 0; p < planes.Length; p++)
            {
                Plane pl = planes[p];
                float d = pl.normal.x * center.x + pl.normal.y * center.y + pl.normal.z * center.z + pl.distance;
                if (d < -radius) return false; // fully outside this plane
            }
            return true;
        }

        // ---- instance buffer ----

        // Float-word offsets into the Raw buffer (regions sized by capacity).
        int ObjRegionStart => HeaderFloats;
        int WorldRegionStart => HeaderFloats + _capacity * FloatsPerMatrix;
        int ParamsRegionStart => HeaderFloats + _capacity * FloatsPerMatrix * 2;

        void EnsureCapacity(int neededInstances)
        {
            int needed = neededInstances + 1; // include reserved instance 0
            if (needed <= _capacity && _instanceBuffer != null) return;

            int newCap = math.max(256, math.ceilpow2(needed));
            // ObjToWorld + WorldToObject + _ChannelParams regions.
            int totalFloats = HeaderFloats + newCap * (FloatsPerMatrix * 2 + FloatsPerParams);

            var newBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, totalFloats, sizeof(float));

            // Zero the whole buffer (header reserved + uninitialised instance slots).
            var zero = new NativeArray<float>(totalFloats, Allocator.Temp, NativeArrayOptions.ClearMemory);
            newBuffer.SetData(zero);
            zero.Dispose();

            _instanceBuffer?.Dispose();
            _instanceBuffer = newBuffer;
            _capacity = newCap;

            // (Re)register the single batch over the new buffer with the metadata layout. Offsets in BYTES.
            if (_batchValid) { _brg.RemoveBatch(_batchId); _batchValid = false; }

            var metadata = new NativeArray<MetadataValue>(3, Allocator.Temp);
            metadata[0] = new MetadataValue { NameID = Shader.PropertyToID("unity_ObjectToWorld"), Value = 0x80000000 | (uint)(ObjRegionStart * sizeof(float)) };
            metadata[1] = new MetadataValue { NameID = Shader.PropertyToID("unity_WorldToObject"), Value = 0x80000000 | (uint)(WorldRegionStart * sizeof(float)) };
            metadata[2] = new MetadataValue { NameID = Shader.PropertyToID("_ChannelParams"), Value = 0x80000000 | (uint)(ParamsRegionStart * sizeof(float)) };
            _batchId = _brg.AddBatch(metadata, _instanceBuffer.bufferHandle);
            _batchValid = true;
            metadata.Dispose();

            for (int i = 0; i < _live.Count; i++)
            {
                WriteInstance(_live[i].Index, _live[i].ObjectToWorld);
                WriteChannelParams(_live[i].Index, _live[i].HasChannels ? _live[i].SliceBase : 0,
                                                   _live[i].HasChannels ? _live[i].ChannelCount : 0);
            }
        }

        /// <summary>Writes the per-instance _ChannelParams = (sliceBase, channelCount, 0, 0).</summary>
        void WriteChannelParams(int index, int sliceBase, int channelCount)
        {
            var p = new NativeArray<float>(FloatsPerParams, Allocator.Temp);
            p[0] = sliceBase; p[1] = channelCount; p[2] = 0f; p[3] = 0f;
            // index*stride (not index-1): DOTS reads at instanceID*stride; see WriteInstance.
            _instanceBuffer.SetData(p, 0, ParamsRegionStart + index * FloatsPerParams, FloatsPerParams);
            p.Dispose();
        }

        static readonly float4[] LodDebugColors =
        {
            new float4(0.2f, 1.0f, 0.2f, 1f),  // LOD0 green
            new float4(1.0f, 0.9f, 0.2f, 1f),  // LOD1 yellow
            new float4(1.0f, 0.4f, 0.2f, 1f),  // LOD2 orange/red
            new float4(0.8f, 0.2f, 0.9f, 1f),  // LOD3+ purple
        };

        /// <summary>Writes the packed ObjectToWorld + WorldToObject (float3x4 = 12 floats each) for an instance.</summary>
        void WriteInstance(int index, float4x4 objToWorld)
        {
            float4x4 worldToObj = math.inverse(objToWorld);

            var obj = new NativeArray<float>(FloatsPerMatrix, Allocator.Temp);
            PackFloat3x4(objToWorld, obj);
            var wor = new NativeArray<float>(FloatsPerMatrix, Allocator.Temp);
            PackFloat3x4(worldToObj, wor);

            // DOTS reads per-instance data at metadataOffset + instanceID*stride, where instanceID == Index
            // (the value put in visibleInstances). So write at index*stride, NOT (index-1)*stride. Instance 0
            // is reserved. (This off-by-one was invisible for matrices since they're all identity, but it
            // shifted each section's per-instance _ChannelParams to its neighbour → displaced paint.)
            int objStart = ObjRegionStart + index * FloatsPerMatrix;
            int worStart = WorldRegionStart + index * FloatsPerMatrix;
            _instanceBuffer.SetData(obj, 0, objStart, FloatsPerMatrix);
            _instanceBuffer.SetData(wor, 0, worStart, FloatsPerMatrix);

            obj.Dispose();
            wor.Dispose();
        }

        /// <summary>Packs a matrix as float3x4 column-major (3 columns of xyz, translation last) — the layout
        /// SRP DOTS instancing expects for unity_ObjectToWorld / unity_WorldToObject.</summary>
        static void PackFloat3x4(float4x4 m, NativeArray<float> dst)
        {
            // 4 columns × 3 rows (x,y,z). Translation is column c3.
            dst[0] = m.c0.x; dst[1] = m.c0.y; dst[2] = m.c0.z;
            dst[3] = m.c1.x; dst[4] = m.c1.y; dst[5] = m.c1.z;
            dst[6] = m.c2.x; dst[7] = m.c2.y; dst[8] = m.c2.z;
            dst[9] = m.c3.x; dst[10] = m.c3.y; dst[11] = m.c3.z;
        }

        // ---- helpers ----

        static unsafe T* Malloc<T>(int count) where T : unmanaged
            => (T*)Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Malloc(
                sizeof(T) * count, UnsafeUtility.AlignOf<T>(), Allocator.TempJob);

        public void Dispose()
        {
            foreach (var slot in _slots.Values)
                DestroyLods(slot);
            _slots.Clear();
            _live.Clear();

            if (_batchValid) { _brg.RemoveBatch(_batchId); _batchValid = false; }
            _brg.UnregisterMaterial(_materialId);
            if (_lodMaterials != null)
            {
                for (int i = 0; i < _lodMaterials.Length; i++)
                {
                    _brg.UnregisterMaterial(_lodMaterialIds[i]);
                    if (_lodMaterials[i] != null)
                    {
                        if (Application.isPlaying) UnityEngine.Object.Destroy(_lodMaterials[i]);
                        else UnityEngine.Object.DestroyImmediate(_lodMaterials[i]);
                    }
                }
                _lodMaterials = null;
            }
            if (_hasSharedChannels)
            {
                _brg.UnregisterMaterial(_sharedChannelMaterialId);
                DestroyObj(_sharedChannelMaterial);
            }
            _brg.Dispose();
            _instanceBuffer?.Dispose();
            _instanceBuffer = null;
            _atlas?.Dispose();
            _atlas = null;
        }
    }
}
