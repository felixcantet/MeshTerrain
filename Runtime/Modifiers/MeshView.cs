using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Fca.MeshTerrain
{
    /// <summary>
    /// A bounded read/write view over a <see cref="MeshData"/> (and its <see cref="WeightLayerSet"/>),
    /// handed to a modifier so it can only touch vertices inside its declared bounds and only the
    /// components it declared. Unity port of UE <c>FMeshView</c> (see
    /// <c>doc/source/.../MeshPartitionMeshView.cpp</c>), <b>simple path only</b> — no <c>DynamicSubmesh</c>
    /// (topology) support this pass.
    ///
    /// Managed-first (matches UE's C++ loops). <see cref="Build"/> collects the in-bounds vertices and
    /// caches the requested attributes indexed by <i>view index</i> (0..<see cref="VertexCount"/>);
    /// modifiers read/write by view index; <see cref="Writeback"/> scatters changes back to the source by
    /// vertex id. Burst-optimising these loops is a later pass.
    /// </summary>
    public sealed class MeshView
    {
        // Not readonly: MeshData is a struct holding NativeArray handles. Writing through those handles in
        // Writeback() mutates a "member of" this field, which the compiler forbids on a readonly value-type
        // field (CS1648) — even though the native memory, not the field, is what changes.
        MeshData _mesh;
        readonly WeightLayerSet _weights;
        readonly Bounds _bounds;
        readonly MeshViewComponents _read;
        readonly MeshViewComponents _write;
        readonly List<string> _usedChannels;

        // view index -> source vertex id
        readonly List<int> _vertexIds = new();
        // cached attributes, indexed by view index
        readonly List<float3> _positions = new();
        readonly List<float2> _uvs = new();
        // channel name -> cached weights (indexed by view index)
        readonly Dictionary<string, float[]> _weightChannels = new();

        public MeshView(MeshData mesh, WeightLayerSet weights, Bounds bounds,
            MeshViewComponents read, MeshViewComponents write, List<string> usedChannels)
        {
            _mesh = mesh;
            _weights = weights;
            _bounds = bounds;
            _read = read;
            _write = write;
            _usedChannels = usedChannels;
        }

        public int VertexCount => _vertexIds.Count;

        bool Reads(MeshViewComponents c) => (_read & c) != 0;
        bool Writes(MeshViewComponents c) => (_write & c) != 0;
        bool Touches(MeshViewComponents c) => ((_read | _write) & c) != 0;

        /// <summary>
        /// Collects the in-bounds vertices and caches the requested components. Mirrors
        /// <c>FMeshView::Build</c> (non-submesh branch).
        /// </summary>
        public void Build()
        {
            bool needPos = Touches(MeshViewComponents.VertexPos);
            bool needUV = Touches(MeshViewComponents.UV);
            bool needWeight = Touches(MeshViewComponents.Weight);

            for (int vid = 0; vid < _mesh.VertexCount; vid++)
            {
                float3 p = _mesh.Vertices[vid];
                if (!_bounds.Contains(p)) continue;

                _vertexIds.Add(vid);
                if (needPos) _positions.Add(p);
                if (needUV) _uvs.Add(_mesh.HasSourceUV0 ? _mesh.SourceUV0[vid] : float2.zero);
            }

            if (needWeight && _weights != null && _usedChannels != null)
            {
                foreach (var channel in _usedChannels)
                {
                    var cache = new float[_vertexIds.Count];
                    if (_weights.TryGetLayer(channel, out var layer))
                    {
                        for (int i = 0; i < _vertexIds.Count; i++)
                            cache[i] = layer[_vertexIds[i]];
                    }
                    _weightChannels[channel] = cache;
                }
            }
        }

        // ---- Reads ----

        public float3 GetVertexPos(int viewIndex)
        {
            Debug.Assert(Reads(MeshViewComponents.VertexPos),
                "MeshView.GetVertexPos: VertexPos not declared in the read mask.");
            return _positions[viewIndex];
        }

        public float2 GetVertexUV(int viewIndex)
        {
            Debug.Assert(Reads(MeshViewComponents.UV),
                "MeshView.GetVertexUV: UV not declared in the read mask.");
            return _uvs[viewIndex];
        }

        public float GetVertexAttributeWeight(string channel, int viewIndex)
        {
            Debug.Assert(Reads(MeshViewComponents.Weight),
                "MeshView.GetVertexAttributeWeight: Weight not declared in the read mask.");
            return _weightChannels.TryGetValue(channel, out var cache) ? cache[viewIndex] : 0f;
        }

        // ---- Writes ----

        public void SetVertexPos(int viewIndex, float3 newPos)
        {
            if (!Writes(MeshViewComponents.VertexPos))
            {
                Debug.Assert(false, "MeshView.SetVertexPos: VertexPos not declared in the write mask.");
                return;
            }
            // UE FMeshView::SetVertexPos rejects moves outside the view bounds.
            if (!_bounds.Contains(newPos))
            {
                Debug.Assert(false, "MeshView.SetVertexPos: attempted to move a vertex outside the view bounds.");
                return;
            }
            _positions[viewIndex] = newPos;
        }

        public void SetVertexUV(int viewIndex, float2 newUV)
        {
            if (!Writes(MeshViewComponents.UV))
            {
                Debug.Assert(false, "MeshView.SetVertexUV: UV not declared in the write mask.");
                return;
            }
            _uvs[viewIndex] = newUV;
        }

        public void SetVertexAttributeWeight(string channel, int viewIndex, float weight)
        {
            if (!Writes(MeshViewComponents.Weight))
            {
                Debug.Assert(false, "MeshView.SetVertexAttributeWeight: Weight not declared in the write mask.");
                return;
            }
            if (!_weightChannels.TryGetValue(channel, out var cache))
            {
                Debug.Assert(false,
                    $"MeshView.SetVertexAttributeWeight: channel '{channel}' not in the instance's UsedChannels.");
                return;
            }
            cache[viewIndex] = weight;
        }

        /// <summary>
        /// Scatters cached writes back to the source <see cref="MeshData"/>/<see cref="WeightLayerSet"/>
        /// by vertex id. Mirrors <c>FMeshView::Writeback</c> (non-submesh branch).
        /// </summary>
        public void Writeback()
        {
            if (Writes(MeshViewComponents.VertexPos))
            {
                for (int i = 0; i < _vertexIds.Count; i++)
                    _mesh.Vertices[_vertexIds[i]] = _positions[i];
            }

            if (Writes(MeshViewComponents.UV) && _mesh.HasSourceUV0)
            {
                for (int i = 0; i < _vertexIds.Count; i++)
                    _mesh.SourceUV0[_vertexIds[i]] = _uvs[i];
            }

            if (Writes(MeshViewComponents.Weight) && _weights != null)
            {
                foreach (var kvp in _weightChannels)
                {
                    var layer = _weights.InitializeLayer(kvp.Key, _mesh.VertexCount);
                    float[] cache = kvp.Value;
                    for (int i = 0; i < _vertexIds.Count; i++)
                        layer[_vertexIds[i]] = cache[i];
                }
            }
        }
    }
}
