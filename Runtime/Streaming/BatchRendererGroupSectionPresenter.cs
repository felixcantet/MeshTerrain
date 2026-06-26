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
        //   [ 16-float zeroed header ][ ObjectToWorld region: 12 floats * capacity ][ WorldToObject region ]
        // Each matrix is a packed float3x4 (12 floats). Metadata offsets below are in BYTES.
        const int HeaderFloats = 16;                 // 64-byte reserved header (instance 0)
        const int FloatsPerMatrix = 12;              // float3x4

        readonly BatchRendererGroup _brg;
        readonly Material _material;
        readonly BatchMaterialID _materialId;

        // One batch over a growable instance buffer; meshes are registered per section.
        GraphicsBuffer _instanceBuffer;
        BatchID _batchId;
        bool _batchValid;
        int _capacity;

        sealed class Slot : ISectionHandle
        {
            public int3 Coord { get; set; }
            public int Index;            // instance index in the buffer (>=1; 0 is reserved)
            public Mesh Mesh;            // owned LOD0 mesh
            public BatchMeshID MeshId;
            public float4x4 ObjectToWorld;
            public Bounds WorldBounds;
        }

        readonly Dictionary<int3, Slot> _slots = new();
        readonly List<Slot> _live = new();              // dense list for draw emission
        readonly Transform _root;                        // parent for bookkeeping only (no per-section GO)

        public BatchRendererGroupSectionPresenter(Material material, Transform root = null)
        {
            _material = material != null ? material : new Material(Shader.Find("Mesh Terrain/Instanced Flat (URP, BRG)"));
            _root = root;

            _brg = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);
            _materialId = _brg.RegisterMaterial(_material);

            // Global bounds so the group is never culled wholesale; per-section bounds drive real culling later.
            _brg.SetGlobalBounds(new Bounds(Vector3.zero, Vector3.one * 1_000_000f));

            EnsureCapacity(256);
        }

        // ---- ISectionPresenter ----

        public ISectionHandle Present(CookedSection cooked, Transform root)
        {
            // Present LOD0. The cooked LOD vertices are already in WORLD space, so the mesh is uploaded as-is
            // and drawn with an IDENTITY per-instance transform — no per-section translation. This is the
            // intended design for streamed terrain (the cook produces absolute geometry), and it keeps the
            // instance buffer trivial (all identity) so the transform path can't misplace tiles.
            MeshData lod0 = cooked.HasBakedLods ? cooked.Lods[0].Mesh : cooked.Mesh;

            Mesh mesh = MeshDataConversions.ToRenderMesh(lod0, $"BRG_{cooked.Coord.x}_{cooked.Coord.y}_{cooked.Coord.z}");

            var slot = new Slot
            {
                Coord = cooked.Coord,
                Mesh = mesh,
                MeshId = _brg.RegisterMesh(mesh),
                ObjectToWorld = float4x4.identity,
                WorldBounds = mesh.bounds,
            };

            slot.Index = _live.Count + 1;     // instance 0 reserved
            _live.Add(slot);
            _slots[cooked.Coord] = slot;

            EnsureCapacity(_live.Count + 1);
            WriteInstance(slot.Index, slot.ObjectToWorld);

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

            _brg.UnregisterMesh(slot.MeshId);
            if (slot.Mesh != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(slot.Mesh);
                else UnityEngine.Object.DestroyImmediate(slot.Mesh);
                slot.Mesh = null;
            }
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

            var draws = cullingOutput.drawCommands;
            var output = new BatchCullingOutputDrawCommands();

            // One draw command per section (prototype: per-mesh, since each section is a distinct mesh).
            // Visible-instance array is 1:1 with sections for now (no GPU cull yet).
            output.visibleInstanceCount = count;
            output.visibleInstances = Malloc<int>(count);
            for (int i = 0; i < count; i++) output.visibleInstances[i] = _live[i].Index;

            output.drawCommandCount = count;
            output.drawCommands = Malloc<BatchDrawCommand>(count);
            for (int i = 0; i < count; i++)
            {
                output.drawCommands[i] = new BatchDrawCommand
                {
                    visibleOffset = (uint)i,
                    visibleCount = 1,
                    batchID = _batchId,
                    materialID = _materialId,
                    meshID = _live[i].MeshId,
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
                drawCommandsCount = (uint)count,
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

            draws[0] = output;
            return new JobHandle();
        }

        // ---- instance buffer ----

        // Float-word offsets into the Raw buffer (regions sized by capacity).
        int ObjRegionStart => HeaderFloats;
        int WorldRegionStart => HeaderFloats + _capacity * FloatsPerMatrix;

        void EnsureCapacity(int neededInstances)
        {
            int needed = neededInstances + 1; // include reserved instance 0
            if (needed <= _capacity && _instanceBuffer != null) return;

            int newCap = math.max(256, math.ceilpow2(needed));
            int totalFloats = HeaderFloats + newCap * FloatsPerMatrix * 2; // ObjToWorld + WorldToObject regions

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

            var metadata = new NativeArray<MetadataValue>(2, Allocator.Temp);
            uint objOffsetBytes = (uint)(ObjRegionStart * sizeof(float));
            uint worldOffsetBytes = (uint)(WorldRegionStart * sizeof(float));
            metadata[0] = new MetadataValue { NameID = Shader.PropertyToID("unity_ObjectToWorld"), Value = 0x80000000 | objOffsetBytes };
            metadata[1] = new MetadataValue { NameID = Shader.PropertyToID("unity_WorldToObject"), Value = 0x80000000 | worldOffsetBytes };
            _batchId = _brg.AddBatch(metadata, _instanceBuffer.bufferHandle);
            _batchValid = true;
            metadata.Dispose();

            // Re-write all live instances into the resized buffer.
            for (int i = 0; i < _live.Count; i++)
                WriteInstance(_live[i].Index, _live[i].ObjectToWorld);
        }

        /// <summary>Writes the packed ObjectToWorld + WorldToObject (float3x4 = 12 floats each) for an instance.</summary>
        void WriteInstance(int index, float4x4 objToWorld)
        {
            float4x4 worldToObj = math.inverse(objToWorld);

            var obj = new NativeArray<float>(FloatsPerMatrix, Allocator.Temp);
            PackFloat3x4(objToWorld, obj);
            var wor = new NativeArray<float>(FloatsPerMatrix, Allocator.Temp);
            PackFloat3x4(worldToObj, wor);

            int objStart = ObjRegionStart + (index - 1) * FloatsPerMatrix;
            int worStart = WorldRegionStart + (index - 1) * FloatsPerMatrix;
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
            {
                _brg.UnregisterMesh(slot.MeshId);
                if (slot.Mesh != null)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(slot.Mesh);
                    else UnityEngine.Object.DestroyImmediate(slot.Mesh);
                }
            }
            _slots.Clear();
            _live.Clear();

            if (_batchValid) { _brg.RemoveBatch(_batchId); _batchValid = false; }
            _brg.UnregisterMaterial(_materialId);
            _brg.Dispose();
            _instanceBuffer?.Dispose();
            _instanceBuffer = null;
        }
    }
}
